using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using LinqToDB.Common;
using LinqToDB.Data;
using LinqToDB.Interceptors;
using LinqToDB.Internal.Common;
using LinqToDB.Internal.DataProvider;
using LinqToDB.Internal.DataProvider.Translation;
using LinqToDB.Internal.Expressions;
using LinqToDB.Internal.Extensions;
using LinqToDB.Internal.Infrastructure;
using LinqToDB.Internal.Interceptors;
using LinqToDB.Internal.Linq;
using LinqToDB.Internal.Remote;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Linq.Translation;
using LinqToDB.Mapping;
using LinqToDB.Metrics;

namespace LinqToDB.Remote
{
	public class LinqService : ILinqService
	{
		private MappingSchema? _serializationMappingSchema;

		public bool    AllowUpdates    { get; set; }
		public string? RemoteClientTag { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether clients of this service are trusted to send any SQL.
		/// Default is <see langword="false"/>: requests are limited to allowed types, connection configurations and SQL text.
		/// This is independent of <see cref="AllowUpdates"/>.
		/// </summary>
		/// <remarks>
		/// The policy limits what SQL a client can run. It is not authorization: a permitted query can read any mapped table,
		/// so authenticate and authorize requests in the transport and use a database login with only the rights clients need.
		/// </remarks>
		public bool TrustedClient { get; set; }

		/// <summary>
		/// Gets connection configurations an untrusted client may select. When empty, only the default configuration
		/// (<see langword="null"/>) is accepted. When not empty, only listed values are accepted, including <see langword="null"/>.
		/// Not applied when <see cref="TrustedClient"/> is <see langword="true"/>.
		/// </summary>
		public ICollection<string?> AllowedConfigurations { get; } = new HashSet<string?>(StringComparer.Ordinal);

		/// <summary>
		/// Gets assemblies an untrusted client may name for entity, enum and extension builder types.
		/// Not applied when <see cref="TrustedClient"/> is <see langword="true"/>.
		/// </summary>
		public ICollection<Assembly> MappingAssemblies { get; } = new HashSet<Assembly>();

		/// <summary>
		/// Gets assemblies whose <see cref="SqlTextCatalogAttribute"/> catalogs are read, typically a client assembly's generated
		/// catalog. No type of these assemblies is accepted from clients; use <see cref="MappingAssemblies"/> for that.
		/// When the server cannot load the assembly, export its catalog with <see cref="SqlTextCatalog.Export(System.IO.TextWriter, Assembly)"/>
		/// and load the file into <see cref="SqlText"/> instead.
		/// Not applied when <see cref="TrustedClient"/> is <see langword="true"/>.
		/// </summary>
		public ICollection<Assembly> SqlTextAssemblies { get; } = new HashSet<Assembly>();

		/// <summary>
		/// Gets host-maintained SQL text accepted from untrusted clients.
		/// Not applied when <see cref="TrustedClient"/> is <see langword="true"/>.
		/// </summary>
		public SqlTextCatalog SqlText { get; } = new();

		void CheckConfiguration(string? configuration)
		{
			if (TrustedClient)
				return;

			var isAllowed = AllowedConfigurations.Count == 0
				? configuration == null
				: AllowedConfigurations.Contains(configuration);

			if (!isAllowed)
			{
				var rejection = new RemoteRejection(
					RemoteRejectionReason.ConfigurationNotAllowed,
					null,
					configuration ?? "<default>",
					"Configuration",
					"Add the configuration to LinqService.AllowedConfigurations if clients may use it.");

				throw new RemoteRequestRejectedException([rejection], false, null, configuration);
			}
		}

		RemoteQueryPolicy? CreatePolicy(DataConnection db, string? configuration)
		{
			if (TrustedClient)
				return null;

			var policy = RemoteQueryPolicy.Create(db, MappingAssemblies, configuration, SqlText, SqlTextAssemblies);

			ReportStaleRemovals(configuration, policy);

			return policy;
		}

		readonly ConcurrentDictionary<(string? Configuration, int Version), bool> _staleRemovalsReported = new();

		// once per configuration and catalog version: a removal that matches nothing is usually a typo that leaves the text allowed
		void ReportStaleRemovals(string? configuration, RemoteQueryPolicy policy)
		{
			if (!_staleRemovalsReported.TryAdd((configuration, SqlText.Version), true))
				return;

			foreach (var (kind, text) in SqlText.GetStaleRemovals(policy.TextSources))
			{
				DataConnection.WriteTraceLine(
					string.Create(CultureInfo.InvariantCulture, $"LinqService.SqlText removal matches no entry for configuration '{configuration ?? "<default>"}': {kind} '{text}'."),
					nameof(LinqService),
					TraceLevel.Warning);
			}
		}

		/// <summary>
		/// Lists the effective SQL text catalog for untrusted clients of <paramref name="configuration"/>: library, provider,
		/// <see cref="MappingAssemblies"/> and <see cref="SqlTextAssemblies"/> entries with their removal state, assembly removals
		/// under the assembly that declares them, host additions, and removals that match nothing.
		/// </summary>
		/// <param name="configuration">Connection configuration to list the catalog for.</param>
		/// <returns>Entries ordered by kind and text. Pass them to <see cref="SqlTextCatalog.Export(System.IO.TextWriter, IEnumerable{SqlTextEntry})"/> to save the full set.</returns>
		/// <remarks>Opens a data context for <paramref name="configuration"/> through <see cref="CreateDataContext"/>, without checking <see cref="AllowedConfigurations"/>.</remarks>
		public IReadOnlyList<SqlTextEntry> DumpSqlText(string? configuration = null)
		{
			using var db = CreateDataContext(configuration);

			return SqlText.Dump(RemoteQueryPolicy.GetTextSources(db, MappingAssemblies, SqlTextAssemblies), RemoteQueryPolicy.GetDialect(db));
		}

		public MappingSchema? MappingSchema
		{
			get;
			set
			{
				field = value;
				_serializationMappingSchema = value != null
					? MappingSchema.CombineSchemas(Internal.Remote.SerializationMappingSchema.Instance, value)
					: Internal.Remote.SerializationMappingSchema.Instance;
			}
		}

		internal MappingSchema SerializationMappingSchema => _serializationMappingSchema ??=
			MappingSchema != null
				? MappingSchema.CombineSchemas(Internal.Remote.SerializationMappingSchema.Instance, MappingSchema)
				: Internal.Remote.SerializationMappingSchema.Instance;

		public static Func<string, Type?> TypeResolver = _ => null;

		public LinqService()
		{
		}

		public LinqService(MappingSchema? mappingSchema)
		{
			MappingSchema = mappingSchema;
		}

		public virtual DataConnection CreateDataContext(string? configuration)
		{
			var dc = new DataConnection(configuration) { Tag = RemoteClientTag };
			if (MappingSchema != null)
				dc.AddMappingSchema(MappingSchema);
			return dc;
		}

		protected virtual void ValidateQuery(LinqServiceQuery query)
		{
			if (!AllowUpdates && query.Statement.QueryType != QueryType.Select)
				throw new LinqToDBException("Insert/Update/Delete requests are not allowed by the service policy.");
		}

		protected virtual void HandleException(Exception exception)
		{
		}

		#region ILinqService Members

		[SuppressMessage("AsyncUsage", "AsyncFixer04:Fire-and-forget async call inside an using block", Justification = "False positive")]
		public virtual Task<LinqServiceInfo> GetInfoAsync(string? configuration, CancellationToken cancellationToken = default)
		{
			CheckConfiguration(configuration);

			using var ctx = CreateDataContext(configuration);

			var serviceProvider = ((IInfrastructure<IServiceProvider>)ctx.DataProvider).Instance;

			return Task.FromResult(new LinqServiceInfo()
			{
				MappingSchemaType        = ctx.DataProvider.MappingSchema.GetType().AssemblyQualifiedName!,
				MethodCallTranslatorType = serviceProvider.GetRequiredService<IMemberTranslator>().GetType().AssemblyQualifiedName!,
				MemberConverterType      = serviceProvider.GetRequiredService<IMemberConverter>().GetType().AssemblyQualifiedName!,
				DmlServiceType           = serviceProvider.GetService<IDmlService>()?.GetType().AssemblyQualifiedName,
				SqlBuilderType           = ctx.DataProvider.CreateSqlBuilder(ctx.MappingSchema, ctx.Options).GetType().AssemblyQualifiedName!,
				SqlOptimizerType         = ctx.DataProvider.GetSqlOptimizer(ctx.Options).GetType().AssemblyQualifiedName!,
				SqlProviderFlags         = ctx.DataProvider.SqlProviderFlags,
				SupportedTableOptions    = ctx.DataProvider.SupportedTableOptions,
			});
		}

		public async Task<int> ExecuteNonQueryAsync(
			string?           configuration,
			string            queryData,
			CancellationToken cancellationToken = default)
		{
			try
			{
				CheckConfiguration(configuration);

				var db = CreateDataContext(configuration);
				await using var _1 = db.ConfigureAwait(false);

				var query = LinqServiceSerializer.Deserialize(SerializationMappingSchema, db.MappingSchema, db.Options, queryData, CreatePolicy(db, configuration));

				ValidateQuery(query);

				await using var _2 = (db.DataProvider.ExecuteScope(db) ?? EmptyIAsyncDisposable.Instance).ConfigureAwait(false);

				if (query.QueryHints?.Count > 0) db.NextQueryHints.AddRange(query.QueryHints);

				return await DataConnection.QueryRunner.ExecuteNonQueryAsync(
					db,
					new QueryContext(query.Statement, query.DataOptions),
					new SqlParameterValues(),
					cancellationToken
				).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				HandleException(exception);
				throw;
			}
		}

		public async Task<string?> ExecuteScalarAsync(
			string? configuration,
			string queryData,
			CancellationToken cancellationToken = default)
		{
			try
			{
				CheckConfiguration(configuration);

				var db = CreateDataContext(configuration);
				await using var _1 = db.ConfigureAwait(false);

				var query = LinqServiceSerializer.Deserialize(SerializationMappingSchema, db.MappingSchema, db.Options, queryData, CreatePolicy(db, configuration));

				ValidateQuery(query);

				await using var _2 = (db.DataProvider.ExecuteScope(db) ?? EmptyIAsyncDisposable.Instance).ConfigureAwait(false);

				if (query.QueryHints?.Count > 0) db.NextQueryHints.AddRange(query.QueryHints);

				var scalar = await DataConnection.QueryRunner.ExecuteScalarAsync(
					db,
					new QueryContext(query.Statement, query.DataOptions),
					parameterValues: null,
					cancellationToken
				).ConfigureAwait(false);

				var result = ProcessScalar(scalar);

				return result;
			}
			catch (Exception exception)
			{
				HandleException(exception);
				throw;
			}
		}

		private string? ProcessScalar(object? scalar)
		{
			string? result = null;

			if (!scalar.IsNullValue)
			{
				var lsr = new LinqServiceResult
				{
					QueryID    = Guid.NewGuid(),
					FieldCount = 1,
					RowCount   = 1,
					FieldNames = ["scalar"],
					FieldTypes = [scalar.GetType()],
					Data       =
					[
						[
							SerializationConverter.Serialize(SerializationMappingSchema, scalar),
						],
					],
				};

				result = LinqServiceSerializer.Serialize(SerializationMappingSchema, lsr);
			}

			return result;
		}

		public async Task<string> ExecuteReaderAsync(
			string?           configuration,
			string            queryData,
			CancellationToken cancellationToken = default)
		{
			try
			{
				CheckConfiguration(configuration);

				var db = CreateDataContext(configuration);
				await using var _1 = db.ConfigureAwait(false);

				var query = LinqServiceSerializer.Deserialize(SerializationMappingSchema, db.MappingSchema, db.Options, queryData, CreatePolicy(db, configuration));

				ValidateQuery(query);

				await using var _2 = (db.DataProvider.ExecuteScope(db) ?? EmptyIAsyncDisposable.Instance).ConfigureAwait(false);

				if (query.QueryHints?.Count > 0) db.NextQueryHints.AddRange(query.QueryHints);

				var rd = await DataConnection.QueryRunner.ExecuteReaderAsync(
					db,
					new QueryContext(query.Statement, query.DataOptions),
					SqlParameterValues.Empty,
					cancellationToken
				).ConfigureAwait(false);

				await using var _3 = rd.ConfigureAwait(false);

				var ret = ProcessDataReaderWrapper(query, db, rd);

				return LinqServiceSerializer.Serialize(SerializationMappingSchema, ret);
			}
			catch (Exception exception)
			{
				HandleException(exception);
				throw;
			}
		}

		public async Task<int> ExecuteBatchAsync(string? configuration, string queryData, CancellationToken cancellationToken = default)
		{
			try
			{
				CheckConfiguration(configuration);

				var db = CreateDataContext(configuration);
				await using var _1 = db.ConfigureAwait(false);

				var data    = LinqServiceSerializer.DeserializeStringArray(SerializationMappingSchema, db.MappingSchema, db.Options, queryData);
				var queries = data.Select(r => LinqServiceSerializer.Deserialize(SerializationMappingSchema, db.MappingSchema, db.Options, r, CreatePolicy(db, configuration))).ToArray();

				foreach (var query in queries)
					ValidateQuery(query);

				await using var _2 = (db.DataProvider.ExecuteScope(db) ?? EmptyIAsyncDisposable.Instance).ConfigureAwait(false);

				await db.BeginTransactionAsync(cancellationToken)
					.ConfigureAwait(false);

				foreach (var query in queries)
				{
					if (query.QueryHints?.Count > 0) db.NextQueryHints.AddRange(query.QueryHints);

					await DataConnection.QueryRunner.ExecuteNonQueryAsync(db, new QueryContext(query.Statement, query.DataOptions), null, cancellationToken)
						.ConfigureAwait(false);
				}

				await db.CommitTransactionAsync(cancellationToken)
					.ConfigureAwait(false);

				return queryData.Length;
			}
			catch (Exception exception)
			{
				HandleException(exception);
				throw;
			}
		}

		#endregion

		private LinqServiceResult ProcessDataReaderWrapper(LinqServiceQuery query, DataConnection db, DataReaderWrapper rd)
		{
			DbDataReader reader;

			if (db is IInterceptable<IUnwrapDataObjectInterceptor> { Interceptor: { } interceptor })
			{
				using (ActivityService.Start(ActivityID.UnwrapDataObjectInterceptorUnwrapDataReader))
					reader = interceptor.UnwrapDataReader(db, rd.DataReader!);
			}
			else
			{
				reader = rd.DataReader!;
			}

			var ret = new LinqServiceResult
			{
				QueryID    = Guid.NewGuid(),
				FieldCount = rd.DataReader!.FieldCount,
				FieldNames = new string[rd.DataReader!.FieldCount],
				FieldTypes = new Type  [rd.DataReader!.FieldCount],
				Data       = new List<string?[]>(),
			};

			var names             = new HashSet<string>(StringComparer.Ordinal);
			var selectExpressions = query.Statement.QueryType switch
			{
				QueryType.Select => query.Statement.SelectQuery!.Select.Columns.Select(c => c.Expression).ToList(),
				QueryType.Insert => ((SqlInsertStatement)query.Statement).Output!.OutputColumns!,
				QueryType.Delete => ((SqlDeleteStatement)query.Statement).Output!.OutputColumns!,
				QueryType.Update => ((SqlUpdateStatement)query.Statement).Output!.OutputColumns!,
				QueryType.Merge  => ((SqlMergeStatement )query.Statement).Output!.OutputColumns!,
				_ => throw new NotSupportedException($"Query type not supported: {query.Statement.QueryType}"),
			};

			for (var i = 0; i < ret.FieldCount; i++)
			{
				var name = rd.DataReader!.GetName(i);
				var idx  = 0;

				if (names.Contains(name))
				{
					while (names.Contains(name = string.Create(CultureInfo.InvariantCulture, $"c{++idx}")))
					{
					}
				}

				names.Add(name);

				ret.FieldNames[i] = name;
				// ugh...
				// still if it fails here due to empty columns - it is a bug in columns generation

				var fieldType      = selectExpressions[i].SystemType!;
				var valueConverter = QueryHelper.GetValueConverter(selectExpressions[i]);
				if (valueConverter != null)
				{
					// value converter applied on client side for both directions
					// here on read we need to prepare expected by converter type
					fieldType = valueConverter.FromProviderExpression.Parameters[0].Type;
				}

				// async compiled query support
				if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Task<>))
					fieldType = fieldType.GetGenericArguments()[0];

				if (fieldType.IsEnum || (fieldType.IsNullableType && fieldType.UnwrapNullableType().IsEnum))
				{
					var stringConverter = db.MappingSchema.GetConverter(new DbDataType(typeof(string)), new DbDataType(fieldType), false, ConversionType.Common);
					if (stringConverter != null)
						fieldType = typeof(string);
					else
					{
						var type = Converter.GetDefaultMappingFromEnumType(db.MappingSchema, fieldType);
						if (type != null)
						{
							fieldType = type;
						}
					}
				}

				ret.FieldTypes[i] = fieldType;
			}

			var columnReaders = new ConvertFromDataReaderExpression.ColumnReader[rd.DataReader!.FieldCount];

			for (var i = 0; i < ret.FieldCount; i++)
				columnReaders[i] = new ConvertFromDataReaderExpression.ColumnReader(db.MappingSchema,
					// converter must be null, see notes above
					ret.FieldTypes[i], i, converter: null, true);

			while (rd.DataReader!.Read())
			{
				var data = new string[rd.DataReader!.FieldCount];

				ret.RowCount++;

				for (var i = 0; i < ret.FieldCount; i++)
				{
					if (!reader.IsDBNull(i))
					{
						var value = columnReaders[i].GetValue(db, reader);

						if (value != null)
							data[i] = SerializationConverter.Serialize(SerializationMappingSchema, value);
					}
				}

				ret.Data.Add(data);
			}

			return ret;
		}

		#region private classes

		sealed class QueryContext(SqlStatement statement, DataOptions dataOptions) : IQueryContext
		{
			public SqlStatement    Statement       { get; } = statement;
			public object?         Context         { get; set; }
			public bool            IsContinuousRun { get; set; }
			public SqlParameter[]? Parameters      { get; set; }
			public AliasesContext? Aliases         { get; set; }
			public DataOptions     DataOptions     { get; } = dataOptions;
		}

		#endregion
	}
}
