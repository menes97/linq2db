using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using LinqToDB.Data;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.Remote;

namespace LinqToDB.Internal.Remote
{
	/// <summary>
	/// How a type read from a remote request is going to be used.
	/// </summary>
	public enum RemoteTypeRole
	{
		/// <summary>Type a value is converted to.</summary>
		Value,
		/// <summary>System type of a data type.</summary>
		DataType,
		/// <summary>Entity type of a table.</summary>
		Object,
		/// <summary>Query or table extension builder.</summary>
		Builder,
	}

	/// <summary>
	/// Rules applied to one request from an untrusted client.
	/// </summary>
	public sealed class RemoteQueryPolicy
	{
		const int MaxRejections = 100;

		static readonly HashSet<string> _coreLibraryNames = new(StringComparer.OrdinalIgnoreCase)
		{
			"mscorlib", "netstandard", "System.Private.CoreLib", "System.Runtime",
		};

		sealed class UnresolvedType(string name)
		{
			public string Name { get; } = name;
		}

		readonly MappingSchema              _schema;
		readonly Dictionary<string,Assembly> _nameAssemblies = new(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<Assembly>          _entityAssemblies;
		readonly HashSet<Assembly>          _builderAssemblies;
		readonly HashSet<Type>              _definedTypes;
		readonly string?                    _provider;
		readonly string?                    _configuration;
		readonly List<RemoteRejection>      _rejections = new();

		bool _isTruncated;

		readonly SqlTextCatalog _catalog;

		RemoteQueryPolicy(MappingSchema schema, IEnumerable<Assembly> nameAssemblies, IEnumerable<Assembly> entityAssemblies, IEnumerable<Assembly> builderAssemblies, string? provider, string? configuration, SqlTextCatalog catalog)
		{
			_catalog           = catalog;
			_schema            = schema;
			_entityAssemblies  = new HashSet<Assembly>(entityAssemblies);
			_builderAssemblies = new HashSet<Assembly>(builderAssemblies);
			_definedTypes      = new HashSet<Type>(schema.GetDefinedTypes());
			_provider          = provider;
			_configuration     = configuration;

			foreach (var assembly in nameAssemblies)
			{
				var name = assembly.GetName().Name;
				if (name != null && !_nameAssemblies.ContainsKey(name))
					_nameAssemblies.Add(name, assembly);
			}
		}

		/// <summary>Creates the policy for a request served by <paramref name="connection"/>.</summary>
		/// <param name="connection">Server connection handling the request.</param>
		/// <param name="mappingAssemblies">Assemblies registered by the host for entity, enum and builder types.</param>
		/// <param name="configuration">Connection configuration requested by the client.</param>
		/// <param name="catalog">Host-maintained SQL text. When <see langword="null"/>, no host text is accepted.</param>
		/// <returns>Policy instance. An instance collects the violations of a single request.</returns>
		public static RemoteQueryPolicy Create(DataConnection connection, IEnumerable<Assembly> mappingAssemblies, string? configuration, SqlTextCatalog? catalog = null)
		{
			var mapping          = mappingAssemblies.ToList();
			var providerAssembly = connection.DataProvider.GetType().Assembly;

			var nameAssemblies = new List<Assembly>
			{
				typeof(object).Assembly,
				typeof(DataConnection).Assembly,
				providerAssembly,
				connection.DataProvider.DataReaderType.Assembly,
			};

			nameAssemblies.AddRange(mapping);
			nameAssemblies.AddRange(connection.MappingSchema.GetRegisteredScalarTypes().Select(static t => t.Assembly));

			var builderAssemblies = new List<Assembly>(mapping) { typeof(DataConnection).Assembly, providerAssembly };

			return new RemoteQueryPolicy(connection.MappingSchema, nameAssemblies, mapping, builderAssemblies, connection.DataProvider.Name, configuration, catalog ?? new SqlTextCatalog());
		}

		/// <summary>Gets the violations collected so far.</summary>
		public IReadOnlyList<RemoteRejection> Rejections => _rejections;

		/// <summary>Resolves a type name without loading any assembly.</summary>
		/// <param name="name">Type name as sent by the client.</param>
		/// <param name="type">Resolved type.</param>
		/// <returns><see langword="true"/> when the name resolves within the allowed assemblies.</returns>
		public bool TryResolveName(string name, out Type? type)
		{
			type = ResolveEntry(name) as Type;
			return type != null;
		}

		internal object? ResolveEntry(string? name)
		{
			if (name == null)
				return null;

			Type? type;

			try
			{
				type = Type.GetType(name, ResolveAssembly, ResolveTypeInAssembly, false);
			}
			catch (Exception ex) when (ex is ArgumentException or TypeLoadException or FileLoadException or BadImageFormatException)
			{
				type = null;
			}

			return (object?)(type ?? LinqService.TypeResolver(name)) ?? new UnresolvedType(name);
		}

		internal object? ArrayEntry(object? element, Func<Type,Type> makeArray)
		{
			return element switch
			{
				null               => null,
				Type type          => makeArray(type),
				UnresolvedType unk => new UnresolvedType(unk.Name + "[]"),
				_                  => throw new InvalidOperationException(),
			};
		}

		Assembly? ResolveAssembly(AssemblyName name)
		{
			if (name.Name == null)
				return null;

			if (_coreLibraryNames.Contains(name.Name))
				return typeof(object).Assembly;

			return _nameAssemblies.TryGetValue(name.Name, out var assembly) ? assembly : null;
		}

		Type? ResolveTypeInAssembly(Assembly? assembly, string name, bool ignoreCase)
		{
			if (assembly != null)
				return assembly.GetType(name, false, ignoreCase);

			foreach (var candidate in _nameAssemblies.Values)
			{
				var type = candidate.GetType(name, false, ignoreCase);
				if (type != null)
					return type;
			}

			return null;
		}

		internal Type? CheckUse(object? entry, RemoteTypeRole role)
		{
			if (entry == null)
				return null;

			var type = entry as Type;

			switch (role)
			{
				case RemoteTypeRole.Value:
					if (type != null && IsValueType(type))
						return type;

					Reject(RemoteRejectionReason.TypeNotAllowed, entry, role, "Register the type with MappingSchema.AddScalarType on the server schema if it is your own value type.");
					return null;

				case RemoteTypeRole.DataType:
					if (type != null)
						return type;

					Reject(RemoteRejectionReason.TypeNotAllowed, entry, role, "Add the assembly declaring the type to LinqService.MappingAssemblies if it is your own type.");
					return typeof(object);

				case RemoteTypeRole.Object:
					return type != null && (_entityAssemblies.Contains(type.Assembly) || _definedTypes.Contains(type)) ? type : null;

				case RemoteTypeRole.Builder:
					if (type != null && _builderAssemblies.Contains(type.Assembly) && typeof(ISqlExtensionBuilder).IsAssignableFrom(type))
						return type;

					Reject(RemoteRejectionReason.BuilderNotAllowed, entry, role, "Add the assembly declaring the builder to LinqService.MappingAssemblies if it is your own builder.");
					return null;

				default:
					throw new InvalidOperationException();
			}
		}

		bool IsValueType(Type type)
		{
			if (type.IsArray)
				return IsValueType(type.GetElementType()!);

			return type == typeof(Sql.SqlID) || _schema.IsScalarType(type);
		}

		void Reject(RemoteRejectionReason reason, object entry, RemoteTypeRole role, string remedy)
		{
			var text = entry is UnresolvedType unresolved ? unresolved.Name : ((Type)entry).FullName ?? entry.ToString()!;

			Add(new RemoteRejection(reason, null, text, role.ToString(), remedy));
		}

		void Add(RemoteRejection rejection)
		{
			if (_rejections.Count < MaxRejections)
				_rejections.Add(rejection);
			else
				_isTruncated = true;
		}

		internal EntityDescriptor? GetEntityDescriptor(Type? type)
		{
			return type != null && (_entityAssemblies.Contains(type.Assembly) || _definedTypes.Contains(type))
				? _schema.GetEntityDescriptor(type)
				: null;
		}

		internal void CheckText(SqlTextKind kind, string text, int argumentCount, string node)
		{
			var reason = _catalog.Check(kind, text);

			if (reason != null)
			{
				var remedy = reason == RemoteRejectionReason.TextRemovedFromCatalog
					? "The text was removed from the catalog by the host."
					: FormattableString.Invariant($"Add it with LinqService.SqlText.Add(SqlTextKind.{kind}, ...) only if this text comes from your own code.");

				Add(new RemoteRejection(reason.Value, kind, text, node, remedy));
				return;
			}

			if (SqlTextCatalog.GetPlaceholderCount(text) > argumentCount)
				Add(new RemoteRejection(RemoteRejectionReason.PlaceholderOutOfRange, kind, text, node, "The text refers to more arguments than the request supplies."));
		}

		internal void RejectIdentifier(string name, string position)
		{
			Add(new RemoteRejection(RemoteRejectionReason.IdentifierNotMapped, null, name, position, "Use a mapped name, or a name of ASCII letters, digits and underscores."));
		}

		internal void RejectNode(string node)
		{
			Add(new RemoteRejection(RemoteRejectionReason.NodeNotAllowed, null, node, node, "This query element is not accepted from untrusted clients."));
		}

		internal void RejectBuilder(string name)
		{
			Add(new RemoteRejection(RemoteRejectionReason.BuilderNotAllowed, null, name, nameof(RemoteTypeRole.Builder), "This builder has no argument table. Set LinqService.TrustedClient if your clients are trusted."));
		}

		internal void CheckResultType(Type type)
		{
			if (!IsValueType(type))
				Add(new RemoteRejection(RemoteRejectionReason.TypeNotAllowed, null, type.FullName ?? type.Name, "ResultColumn", "Register the type with MappingSchema.AddScalarType on the server schema if it is your own value type."));
		}

		internal bool HasRejections => _rejections.Count > 0;

		internal void ThrowIfRejected(Exception? innerException = null)
		{
			if (HasRejections)
				throw new RemoteRequestRejectedException(_rejections, _isTruncated, _provider, _configuration, innerException);
		}
	}
}
