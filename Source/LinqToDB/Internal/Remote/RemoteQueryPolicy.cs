using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using LinqToDB.Analyzers;
using LinqToDB.Common;
using LinqToDB.Data;
using LinqToDB.Internal.Extensions;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.Remote;

namespace LinqToDB.Internal.Remote
{
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

		// LinqToDB.FSharp maps F# options as nullable columns. Only these generic definitions of FSharp.Core are accepted; their
		// type argument resolves through the same rules, as an array element does.
		const string FSharpCoreName = "FSharp.Core";

		static readonly HashSet<string> _optionTypeNames = new(StringComparer.Ordinal)
		{
			"Microsoft.FSharp.Core.FSharpOption`1", "Microsoft.FSharp.Core.FSharpValueOption`1",
		};

		sealed class UnresolvedType(string name)
		{
			public string Name { get; } = name;
		}

		/// <summary>
		/// The part of a policy that depends only on the provider, the mapping schema and the service's assemblies, not on the
		/// request. Built once and shared by every request with the same inputs.
		/// </summary>
		internal sealed record Shared(
			MappingSchema                                                   Schema,
			Dictionary<string,Assembly>                                     NameAssemblies,
			HashSet<Assembly>                                               EntityAssemblies,
			HashSet<Assembly>                                               BuilderAssemblies,
			HashSet<Type>                                                   DefinedTypes,
			string?                                                         Provider,
			SqlTextSources                                                  Sources,
			Assembly                                                        DriverAssembly,
			SqlTextQuoting.Dialect                                          Dialect,
			Type                                                            ProviderType,
			Dictionary<Type, (string Key, SqlExtensionArgumentRole Role)[]> BuilderRoles,
			Assembly?                                                       OptionAssembly);

		readonly MappingSchema              _schema;
		readonly Dictionary<string,Assembly> _nameAssemblies;
		readonly HashSet<Assembly>          _entityAssemblies;
		readonly HashSet<Assembly>          _builderAssemblies;
		readonly HashSet<Type>              _definedTypes;
		readonly string?                    _provider;
		readonly string?                    _configuration;
		readonly List<RemoteRejection>      _rejections = new();

		bool _isTruncated;

		readonly SqlTextCatalog                                                        _catalog;
		readonly SqlTextSources                                                        _sources;
		readonly Assembly                                                              _driverAssembly;
		readonly SqlTextQuoting.Dialect                                                _dialect;
		readonly Type                                                                  _providerType;

		readonly Dictionary<Type, (string Key, SqlExtensionArgumentRole Role)[]>       _builderRoles;
		readonly Assembly?                                                             _optionAssembly;

		RemoteQueryPolicy(Shared shared, string? configuration, SqlTextCatalog catalog)
		{
			_optionAssembly    = shared.OptionAssembly;
			_builderRoles      = shared.BuilderRoles;
			_providerType      = shared.ProviderType;
			_dialect           = shared.Dialect;
			_driverAssembly    = shared.DriverAssembly;
			_sources           = shared.Sources;
			_schema            = shared.Schema;
			_entityAssemblies  = shared.EntityAssemblies;
			_builderAssemblies = shared.BuilderAssemblies;
			_definedTypes      = shared.DefinedTypes;
			_provider          = shared.Provider;
			_nameAssemblies    = shared.NameAssemblies;
			_catalog           = catalog;
			_configuration     = configuration;
		}

		/// <summary>Creates the policy for a request served by <paramref name="connection"/>.</summary>
		/// <param name="connection">Server connection handling the request.</param>
		/// <param name="mappingAssemblies">Assemblies registered by the host for entity, enum and builder types.</param>
		/// <param name="configuration">Connection configuration requested by the client.</param>
		/// <param name="catalog">Host-maintained SQL text. When <see langword="null"/>, no host text is accepted.</param>
		/// <param name="sqlTextAssemblies">Assemblies whose SQL text catalogs are read; none of their types are accepted.</param>
		/// <returns>Policy instance. An instance collects the violations of a single request.</returns>
		public static RemoteQueryPolicy Create(DataConnection connection, IEnumerable<Assembly> mappingAssemblies, string? configuration, SqlTextCatalog? catalog = null, IEnumerable<Assembly>? sqlTextAssemblies = null)
		{
			return new RemoteQueryPolicy(CreateShared(connection, mappingAssemblies, sqlTextAssemblies), configuration, catalog ?? new SqlTextCatalog());
		}

		/// <summary>Creates the policy for one request from a <see cref="Shared"/> part built for the same provider, schema and assemblies.</summary>
		internal static RemoteQueryPolicy Create(Shared shared, string? configuration, SqlTextCatalog catalog)
		{
			return new RemoteQueryPolicy(shared, configuration, catalog);
		}

		internal static Shared CreateShared(DataConnection connection, IEnumerable<Assembly> mappingAssemblies, IEnumerable<Assembly>? sqlTextAssemblies)
		{
			var mapping          = mappingAssemblies.ToList();
			var providerAssembly = connection.DataProvider.GetType().Assembly;

			var nameAssemblies = new Dictionary<string,Assembly>(StringComparer.OrdinalIgnoreCase);

			foreach (var assembly in new[] { typeof(object).Assembly, typeof(DataConnection).Assembly, providerAssembly, connection.DataProvider.DataReaderType.Assembly }
				.Concat(mapping)
				.Concat(connection.MappingSchema.GetRegisteredScalarTypes().Select(static t => t.Assembly)))
			{
				if (assembly.GetName().Name is { } name)
					nameAssemblies.TryAdd(name, assembly);
			}

			// a catalog-only assembly supplies text, never builder roles
			var builderRoles = new Dictionary<Type, (string Key, SqlExtensionArgumentRole Role)[]>();

			foreach (var assembly in new[] { providerAssembly }.Concat(mapping))
				foreach (var pair in SqlTextCatalog.GetBuilderRoles(assembly))
					builderRoles.TryAdd(pair.Key, pair.Value);

			return new Shared(
				Schema            : connection.MappingSchema,
				NameAssemblies    : nameAssemblies,
				EntityAssemblies  : [.. mapping],
				BuilderAssemblies : [.. mapping, typeof(DataConnection).Assembly, providerAssembly],
				DefinedTypes      : [.. connection.MappingSchema.GetDefinedTypes()],
				Provider          : connection.DataProvider.Name,
				Sources           : GetTextSources(connection, mapping, sqlTextAssemblies),
				DriverAssembly    : connection.DataProvider.DataReaderType.Assembly,
				Dialect           : GetDialect(connection),
				ProviderType      : connection.DataProvider.GetType(),
				BuilderRoles      : builderRoles,
				// only an already loaded FSharp.Core: a server that maps F# options has loaded it, and nothing is loaded here
				OptionAssembly    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(static a => string.Equals(a.GetName().Name, FSharpCoreName, StringComparison.Ordinal)));
		}

		/// <summary>Gets the argument roles the provider or a mapping assembly declares for one of its own extension builders.</summary>
		internal bool TryGetBuilderRoles(Type builderType, out (string Key, SqlExtensionArgumentRole Role)[] roles)
		{
			return _builderRoles.TryGetValue(builderType, out roles!);
		}

		/// <summary>
		/// Whether the SQL builder of this provider may render a join hint itself, whatever the hint's builder: one of
		/// <paramref name="families"/>, or any provider type the library does not declare, whose builder may override that.
		/// </summary>
		internal bool MayRenderJoinHint(HashSet<string> families)
		{
			return _providerType.Assembly != typeof(DataConnection).Assembly
				|| GetProviderFamily(_providerType) is { } family && families.Contains(family);
		}

		/// <summary>Gets the quoting contexts of the provider serving <paramref name="connection"/>; all of them for a provider outside the library.</summary>
		internal static SqlTextQuoting.Dialect GetDialect(DataConnection connection)
		{
			return SqlTextQuoting.GetDialect(GetProviderFamily(connection.DataProvider.GetType()));
		}

		/// <summary>Gets the quoting contexts this policy refuses format items in.</summary>
		internal SqlTextQuoting.Dialect Dialect => _dialect;

		/// <summary>
		/// Gets the library, provider, mapping assembly and catalog assembly entries for requests served by <paramref name="connection"/>.
		/// The first source to contribute a text is recorded as its origin. A removal declared by any of them applies to all.
		/// </summary>
		internal static SqlTextSources GetTextSources(DataConnection connection, IEnumerable<Assembly> mappingAssemblies, IEnumerable<Assembly>? sqlTextAssemblies = null)
		{
			var providerAssembly = connection.DataProvider.GetType().Assembly;
			var allowed          = new Dictionary<(SqlTextKind Kind, string Text), SqlTextOrigin>();
			var removed          = new Dictionary<(SqlTextKind Kind, string Text), SqlTextOrigin>();
			var dialect          = GetDialect(connection);
			var merged           = new HashSet<Assembly>();

			Merge(typeof(DataConnection).Assembly, GetProviderFamily(connection.DataProvider.GetType()));
			Merge(providerAssembly, null);

			foreach (var assembly in mappingAssemblies)
				Merge(assembly, null);

			foreach (var assembly in sqlTextAssemblies ?? [])
				Merge(assembly, null);

			var unmatched = new List<(SqlTextKind Kind, string Text)>();

			foreach (var key in removed.Keys)
				if (!allowed.Remove(key))
					unmatched.Add(key);

			return new SqlTextSources(allowed, removed, unmatched);

			void Merge(Assembly assembly, string? family)
			{
				if (!merged.Add(assembly))
					return;

				var source  = assembly.GetName().Name ?? assembly.FullName ?? "";
				var entries = SqlTextCatalog.GetAssemblySources(assembly, family);

				// a text with a format item in a quoted context of this provider is not accepted, whichever assembly declares it
				foreach (var pair in entries.Allowed)
					if (!allowed.ContainsKey(pair.Key) && !SqlTextCatalog.IsRefused(pair.Key.Kind, pair.Key.Text, dialect))
						allowed.Add(pair.Key, new SqlTextOrigin(source, pair.Value));

				foreach (var pair in entries.Removed)
					removed.TryAdd(pair.Key, new SqlTextOrigin(source, pair.Value));
			}
		}

		/// <summary>Gets the library, provider and assembly entries this policy accepts, and the assemblies' removals.</summary>
		internal SqlTextSources TextSources => _sources;

		const string ProviderNamespacePrefix = "LinqToDB.Internal.DataProvider.";

		static string? GetProviderFamily(Type providerType)
		{
			for (var type = providerType; type != null; type = type.BaseType)
			{
				if (type.Namespace?.StartsWith(ProviderNamespacePrefix, StringComparison.Ordinal) == true)
				{
					var rest = type.Namespace.Substring(ProviderNamespacePrefix.Length);
					var dot  = rest.IndexOf('.', StringComparison.Ordinal);

					return dot < 0 ? rest : rest.Substring(0, dot);
				}
			}

			return null;
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

			// checked before any resolver runs: generic and array types nest without bound otherwise. The role still decides
			// what an unresolved name means, so an Object-role name is nulled, not rejected.
			if (name.Length > MaxTypeNameLength || GetNestingDepth(name) > MaxTypeNesting)
				return new UnresolvedType(name);

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
				null                                                  => null,
				Type type when GetArrayDepth(type) >= MaxTypeNesting => new UnresolvedType((type.FullName ?? type.Name) + "[]"),
				Type type                                             => makeArray(type),
				UnresolvedType unk                                    => new UnresolvedType(unk.Name + "[]"),
				_                                                     => throw new InvalidOperationException(),
			};
		}

		const int MaxTypeNameLength = 1024;
		const int MaxTypeNesting    = 8;

		// deepest bracket nesting: generic argument lists and array ranks both open one
		static int GetNestingDepth(string name)
		{
			int depth = 0, max = 0;

			foreach (var c in name)
			{
				if (c == '[')
					max = Math.Max(max, ++depth);
				else if (c == ']')
					depth--;
			}

			return max;
		}

		static int GetArrayDepth(Type type)
		{
			var depth = 0;

			for (var t = type; t.IsArray; t = t.GetElementType()!)
				depth++;

			return depth;
		}

		Assembly? ResolveAssembly(AssemblyName name)
		{
			if (name.Name == null)
				return null;

			if (_coreLibraryNames.Contains(name.Name))
				return typeof(object).Assembly;

			if (_nameAssemblies.TryGetValue(name.Name, out var assembly))
				return assembly;

			return _optionAssembly != null && string.Equals(name.Name, FSharpCoreName, StringComparison.Ordinal) ? _optionAssembly : null;
		}

		Type? ResolveTypeInAssembly(Assembly? assembly, string name, bool ignoreCase)
		{
			if (assembly != null && assembly == _optionAssembly && !_nameAssemblies.ContainsValue(assembly))
				return _optionTypeNames.Contains(name) ? assembly.GetType(name, false, false) : null;

			if (assembly != null)
				return assembly.GetType(name, false, ignoreCase);

			foreach (var candidate in _nameAssemblies.Values)
			{
				var type = candidate.GetType(name, false, ignoreCase);
				if (type != null)
					return type;
			}

			// the wire names a generic type by its full name, without its assembly
			return _optionAssembly != null && _optionTypeNames.Contains(name) ? _optionAssembly.GetType(name, false, false) : null;
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

				// a scalar raw table (FromSqlScalar<T>) is typed by the scalar itself, which is never constructed as an entity
				case RemoteTypeRole.Object:
					return type != null && (_entityAssemblies.Contains(type.Assembly) || _definedTypes.Contains(type) || IsValueType(type)) ? type : null;

				case RemoteTypeRole.Builder:
					if (type != null && _builderAssemblies.Contains(type.Assembly) && typeof(ISqlExtensionBuilder).IsAssignableFrom(type))
						return type;

					Reject(RemoteRejectionReason.BuilderNotAllowed, entry, role, "If it is your own builder, add its assembly to LinqService.MappingAssemblies and declare its argument roles with ISqlExtensionBuilderRoles.");
					return null;

				default:
					throw new InvalidOperationException();
			}
		}

		static bool IsSqlRow(Type type)
		{
			return type.IsGenericType
				&& type.Assembly == typeof(Sql).Assembly
				&& type.DeclaringType == typeof(Sql)
				&& type.Name.StartsWith("SqlRow`", StringComparison.Ordinal);
		}

		bool IsValueType(Type type)
		{
			if (type.IsArray)
				return IsValueType(type.GetElementType()!);

			if (type == typeof(Sql.SqlID) || _schema.IsScalarType(type))
				return true;

			// a row value (Sql.Row(...) in a list, sent as an array of rows) holds only its own values
			if (IsSqlRow(type))
				return type.GetGenericArguments().All(IsValueType);

			var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

			// a generic driver struct (NpgsqlRange<T>) converts its type arguments too
			return unwrapped.IsValueType && unwrapped.Assembly == _driverAssembly && unwrapped.GetGenericArguments().All(IsValueType);
		}

		void Reject(RemoteRejectionReason reason, object entry, RemoteTypeRole role, string remedy)
		{
			var text = entry is UnresolvedType unresolved ? unresolved.Name : ((Type)entry).FullName ?? ((Type)entry).Name;

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

		/// <summary>
		/// Gets the root of the inheritance hierarchy <paramref name="descriptor"/> belongs to, or <see langword="null"/>. A derived type's
		/// descriptor learns its root only once the root's descriptor is built, so an unset root is looked up through the base types.
		/// </summary>
		internal EntityDescriptor? GetInheritanceRoot(EntityDescriptor? descriptor)
		{
			if (descriptor == null)
				return null;

			if (descriptor.InheritanceRoot != null)
				return descriptor.InheritanceRoot;

			for (var type = descriptor.ObjectType.BaseType; type != null; type = type.BaseType)
				if (GetEntityDescriptor(type)?.InheritanceRoot is { } root)
					return root;

			return null;
		}

		internal void CheckText(SqlTextKind kind, string text, int argumentCount, string node)
		{
			var reason = _catalog.Check(kind, text, _sources, _dialect);

			if (reason != null)
			{
				var remedy = reason == RemoteRejectionReason.TextRemovedFromCatalog
					? "The text was removed from the catalog by the host."
					: string.Create(CultureInfo.InvariantCulture, $"Add it with LinqService.SqlText.Add(SqlTextKind.{kind}, ...) only if this text comes from your own code.");

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

		internal void RejectEnum(Type enumType, int value)
		{
			Add(new RemoteRejection(RemoteRejectionReason.InvalidEnumValue, null, value.ToString(CultureInfo.InvariantCulture), enumType.Name, "The value is not defined by the enum."));
		}

		internal void RejectNode(string node)
		{
			Add(new RemoteRejection(RemoteRejectionReason.NodeNotAllowed, null, node, node, "This query element is not accepted from untrusted clients."));
		}

		internal void RejectBuilder(string name)
		{
			Add(new RemoteRejection(RemoteRejectionReason.BuilderNotAllowed, null, name, nameof(RemoteTypeRole.Builder), "The builder has no declared argument roles. If it is your own builder, declare them with ISqlExtensionBuilderRoles in a catalog of its assembly."));
		}

		/// <summary>Rejects a result column type the server would build a reader for, unless it is a value type of the server schema.</summary>
		/// <param name="type">Type from <see cref="GetResultFieldType(MappingSchema, ISqlExpression)"/>.</param>
		internal void CheckResultType(Type? type)
		{
			// read as the raw provider value, with no conversion
			if (type == null || type == typeof(object))
				return;

			if (!IsValueType(type))
				Add(new RemoteRejection(RemoteRejectionReason.TypeNotAllowed, null, type.FullName ?? type.Name, "ResultColumn", "Register the type with MappingSchema.AddScalarType on the server schema if it is your own value type."));
		}

		/// <summary>
		/// Gets the type the service reads a result column as: the expression's type, or the provider type its value converter
		/// expects, without <see cref="Task{TResult}"/>, and an enum's string or default mapping type.
		/// </summary>
		internal Type? GetResultFieldType(ISqlExpression expression) => GetResultFieldType(_schema, expression);

		internal static Type? GetResultFieldType(MappingSchema schema, ISqlExpression expression)
		{
			var fieldType = expression.SystemType;

			// value converter applied on client side for both directions
			// here on read we need to prepare expected by converter type; it also supplies a type the expression lacks
			if (QueryHelper.GetValueConverter(expression) is { } valueConverter)
				fieldType = valueConverter.FromProviderExpression.Parameters[0].Type;

			if (fieldType == null)
				return null;

			// async compiled query support
			if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Task<>))
				fieldType = fieldType.GetGenericArguments()[0];

			if (fieldType.IsEnum || (fieldType.IsNullableType && fieldType.UnwrapNullableType().IsEnum))
			{
				var stringConverter = schema.GetConverter(new DbDataType(typeof(string)), new DbDataType(fieldType), false, ConversionType.Common);

				if (stringConverter != null)
					fieldType = typeof(string);
				else if (Converter.GetDefaultMappingFromEnumType(schema, fieldType) is { } type)
					fieldType = type;
			}

			return fieldType;
		}

		readonly HashSet<string> _dbTypes = new(StringComparer.Ordinal);

		/// <summary>Records a DbType read from the request; checked once the request's tables are known.</summary>
		internal void DeferDbType(string dbType) => _dbTypes.Add(dbType);

		internal IEnumerable<string> DeferredDbTypes => _dbTypes;

		/// <summary>Gets the DbTypes the server schema defines for its registered scalar types.</summary>
		internal IEnumerable<string> GetScalarDbTypes()
		{
			foreach (var type in _schema.GetRegisteredScalarTypes())
				if (_schema.GetDbDataType(type).DbType is { } dbType)
					yield return dbType;
		}

		internal bool HasRejections => _rejections.Count > 0;

		internal void ThrowIfRejected(Exception? innerException = null)
		{
			if (HasRejections)
				throw new RemoteRequestRejectedException(_rejections, _isTruncated, _provider, _configuration, innerException);
		}
	}
}
