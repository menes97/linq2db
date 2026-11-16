using System;
using System.IO;
using System.Linq;
using System.Reflection;

using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.Mapping;
using LinqToDB.Remote;

using NUnit.Framework;

using Shouldly;

using Tests.Model;

namespace Tests.Linq
{
	/// <summary>
	/// The SQL text catalog generated for this assembly, through a real untrusted LinqService in process: what the generator
	/// catalogues is the text the client puts on the wire, and both routes a host has to a client assembly's catalog work
	/// without admitting the assembly's types.
	/// </summary>
	[TestFixture]
	public sealed class SqlTextCatalogRouteTests : TestBase
	{
		public enum Shape
		{
			RawSql,
			FormatItems,
			RawStringLiteral,
			VerbatimLiteral,
			ConstantHole,
			Expression,
			ExtensionTemplate,
			Fragment,
			Function,
			TableExpression,
			WithEager,
			WithInTree,
			TableHint,
			QueryHintList,
			MergeHint,
			StatementHeader,
			SchemaName,
		}

		// Accepted by the validator, but SQL SQLite cannot run (a table hint, MERGE): the green arm checks only that the
		// request is not rejected, the red arm that it is rejected for the expected kind.
		// Not here: a bulk copy table name never crosses the wire (BulkCopy refuses a remote context), and a provider's own
		// hint (PostgreSQL's hint2) reaches it only on that provider, see PostgreSQLHint2.
		static readonly Shape[] _validatorOnly = [Shape.WithEager, Shape.WithInTree, Shape.TableHint, Shape.MergeHint];

		const int ConstantId = 1;

		[Sql.Expression("({0} + 17)", ServerSideOnly = true)]
		static int Plus17(int value) => throw new InvalidOperationException();

		[Sql.Extension("({value} + 17{offset?})", ServerSideOnly = true)]
		static int Plus17Template([ExprParameter] int value) => throw new InvalidOperationException();

		[Sql.Extension("({value} + {offset})", BuilderType = typeof(Plus17FragmentBuilder), ServerSideOnly = true)]
		static int Plus17Fragment([ExprParameter] int value) => throw new InvalidOperationException();

		sealed class Plus17FragmentBuilder : Sql.IExtensionCallBuilder
		{
			public void Build(Sql.ISqlExtensionBuilder builder)
			{
				builder.AddFragment("offset", "16 + 1 /* ROUTE_FRAGMENT */");
			}
		}

		[Sql.Function("unlikely", ServerSideOnly = true)]
		static int Unlikely(int value) => throw new InvalidOperationException();

		[Table("Person")]
		sealed class RoutePerson
		{
			[Column("PersonID"), PrimaryKey] public RouteId Id        { get; set; }
			[Column]                         public string  FirstName { get; set; } = null!;
		}

		enum RouteId
		{
			First = 1,
		}

		static readonly Assembly _tests = typeof(SqlTextCatalogRouteTests).Assembly;
		static readonly Assembly _model = typeof(Person).Assembly;

		// Each text appears in this assembly's generated catalog only; none is in the library catalog.
		static void Run(IDataContext db, DataConnection backend, Shape shape)
		{
			var id = 1;

			switch (shape)
			{
				case Shape.RawSql:
					db
						.FromSql<Person>($"SELECT * FROM Person WHERE PersonID = {id} AND 17 = 17")
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.FormatItems:
					db
						.FromSql<Person>($"SELECT * FROM Person WHERE PersonID = {id,3} AND {id:D1} = {id} /* {{ROUTE_FORMAT}} */")
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.RawStringLiteral:
					db
						.FromSql<Person>($"""SELECT * FROM Person WHERE PersonID = {id} /* ROUTE_RAW */""")
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.VerbatimLiteral:
					db
						.FromSql<Person>($@"SELECT * FROM Person
							WHERE PersonID = {id} /* ROUTE_VERBATIM */")
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.ConstantHole:
					// a constant hole is still an argument of the FormattableString, so a parameter on the wire
					db
						.FromSql<Person>($"SELECT * FROM Person WHERE PersonID = {ConstantId} /* ROUTE_CONSTANT */")
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.ExtensionTemplate:
					db
						.GetTable<Person>()
						.Where(p => p.ID == id)
						.Select(p => Plus17Template(p.ID))
						.ToList()
						.ShouldBe(new[] { 18 });
					break;
				case Shape.Fragment:
					db
						.GetTable<Person>()
						.Where(p => p.ID == id)
						.Select(p => Plus17Fragment(p.ID))
						.ToList()
						.ShouldBe(new[] { 18 });
					break;
				case Shape.Function:
					db
						.GetTable<Person>()
						.Where(p => p.ID == id)
						.Select(p => Unlikely(p.ID))
						.ToList()
						.ShouldBe(new[] { 1 });
					break;
				case Shape.WithEager:
					db
						.GetTable<Person>()
						.With("ROUTE_WITH_EAGER")
						.Where(p => p.ID == id)
						.ToList();
					break;
				case Shape.WithInTree:
				{
					// the second from is a SelectMany lambda, so With stays in the expression tree
					var children = db.GetTable<Child>();
					var query    =
						from p in db.GetTable<Person>()
						from c in children.With("ROUTE_WITH_TREE")
						where p.ID == id && c.ParentID == p.ID
						select c;

					query.ToList();
					break;
				}
				case Shape.TableHint:
					db
						.GetTable<Person>()
						.TableHint("ROUTE_TABLE_HINT")
						.Where(p => p.ID == id)
						.ToList();
					break;
				case Shape.QueryHintList:
					db.QueryHints.Add("/* ROUTE_QUERY_HINT */");

					db
						.GetTable<Person>()
						.Where(p => p.ID == id)
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.MergeHint:
					db
						.GetTable<Person>()
						.Merge("ROUTE_MERGE_HINT")
						.Using(db.GetTable<Person>().Where(p => p.ID == id))
						.OnTargetKey()
						.UpdateWhenMatched()
						.Merge();
					break;
				case Shape.StatementHeader:
					try
					{
						db.CreateTable<Person>(
							tableName       : "RoutePersonHeader",
							statementHeader : "CREATE TABLE {0} /* ROUTE_HEADER */",
							statementFooter : "/* ROUTE_FOOTER */");
					}
					finally
					{
						backend.DropTable<Person>("RoutePersonHeader", throwExceptionIfNotExists: false);
					}

					break;
				case Shape.Expression:
					db
						.GetTable<Person>()
						.Where(p => p.ID == id)
						.Select(p => Plus17(p.ID))
						.ToList()
						.ShouldBe(new[] { 18 });
					break;
				case Shape.TableExpression:
					db
						.GetTable<Person>()
						.WithTableExpression("{0} {1} NOT INDEXED")
						.Where(p => p.ID == id)
						.ToList()
						.Count
						.ShouldBe(1);
					break;
				case Shape.SchemaName:
					db
						.GetTable<Person>()
						.SchemaName("main")
						.Where(p => p.ID == id)
						.ToList()
						.Count
						.ShouldBe(1);
					break;
			}
		}

		static SqlTextKind KindOf(Shape shape) => shape switch
		{
			Shape.RawSql            => SqlTextKind.RawSqlTable,
			Shape.FormatItems       => SqlTextKind.RawSqlTable,
			Shape.RawStringLiteral  => SqlTextKind.RawSqlTable,
			Shape.VerbatimLiteral   => SqlTextKind.RawSqlTable,
			Shape.ConstantHole      => SqlTextKind.RawSqlTable,
			Shape.Expression        => SqlTextKind.Expression,
			Shape.ExtensionTemplate => SqlTextKind.Expression,
			Shape.Fragment          => SqlTextKind.Fragment,
			Shape.Function          => SqlTextKind.Function,
			Shape.TableExpression   => SqlTextKind.TableExpression,
			Shape.WithEager         => SqlTextKind.ExtensionArgument,
			Shape.WithInTree        => SqlTextKind.TableExpression,
			Shape.TableHint         => SqlTextKind.ExtensionArgument,
			Shape.QueryHintList     => SqlTextKind.QueryHint,
			Shape.MergeHint         => SqlTextKind.MergeHint,
			Shape.StatementHeader   => SqlTextKind.StatementHeader,
			Shape.SchemaName        => SqlTextKind.TableName,
			_                       => throw new ArgumentOutOfRangeException(nameof(shape)),
		};

		// The green arm: run the shape and, for a shape SQLite cannot execute, accept any failure but a rejection.
		static void Accept(IDataContext db, DataConnection backend, Shape shape)
		{
			if (!_validatorOnly.Contains(shape))
			{
				Run(db, backend, shape);
				return;
			}

			try
			{
				Run(db, backend, shape);
			}
			catch (Exception ex)
			{
				for (Exception? e = ex; e != null; e = e.InnerException)
					if (e is RemoteRequestRejectedException rejected)
						throw new ShouldAssertException("Rejected: " + string.Join("; ", rejected.Rejections.Select(static r => r.Reason + " " + r.Kind + " " + r.Text)), ex);
			}
		}

		// SQLite only: Run's shapes embed SQLite-specific text (NOT INDEXED, schema main, unlikely()); the catalog route is provider-independent.
		[Test]
		public void CatalogOnlyAssembly_AcceptsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Assemblies." + shape, static s => s.SqlTextAssemblies.Add(_tests));

			Accept(db, backend, shape);
		}

		// SQLite only: as CatalogOnlyAssembly_AcceptsItsText, the same shapes.
		[Test]
		public void WithoutCatalog_RejectsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.None." + shape, static _ => { });

			// a shape can send more than one uncatalogued text (a fragment and its expression, a header and its table name)
			Rejections(() => Run(db, backend, shape))
				.ShouldContain(r => r.Reason == RemoteRejectionReason.TextNotInCatalog && r.Kind == KindOf(shape));
		}

		// SQLite only: as CatalogOnlyAssembly_AcceptsItsText, the same shapes.
		[Test]
		public void ExportedCatalog_LoadedByTheHost_AcceptsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var writer = new StringWriter();

			SqlTextCatalog.Export(writer, _tests);

			var exported = writer.ToString();

			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Export." + shape, s => s.SqlText.Load(new StringReader(exported)));

			Accept(db, backend, shape);
		}

		// PostgreSQL only: SubQueryTableHint is PostgreSQL's, and on another provider the client drops it, so hint2 never
		// reaches the wire there (measured on SQLite.MS: the host without a catalog raised nothing).
		static void PostgreSQLHint2(IDataContext db)
		{
			db
				.GetTable<Person>()
				.Where(p => p.ID == 1)
				.AsPostgreSQL()
				.SubQueryTableHint("/* ROUTE_HINT */", "/* ROUTE_HINT2 */")
				.ToList();
		}

		// PostgreSQL only: see PostgreSQLHint2.
		[Test]
		public void PostgreSQLHint2_CatalogOnlyAssembly_IsAccepted([IncludeDataSources(TestProvName.AllPostgreSQL)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Hint2.Assemblies", static s => s.SqlTextAssemblies.Add(_tests));

			try
			{
				PostgreSQLHint2(db);
			}
			catch (Exception ex)
			{
				// the comment hints are not SQL PostgreSQL runs; only a rejection fails the arm
				for (Exception? e = ex; e != null; e = e.InnerException)
					e.ShouldNotBeOfType<RemoteRequestRejectedException>();
			}
		}

		// PostgreSQL only: see PostgreSQLHint2.
		[Test]
		public void PostgreSQLHint2_WithoutCatalog_IsRejected([IncludeDataSources(TestProvName.AllPostgreSQL)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Hint2.None", static _ => { });

			Rejections(() => PostgreSQLHint2(db))
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.TextNotInCatalog && r.Kind == SqlTextKind.ExtensionArgument && r.Text.Contains("ROUTE_HINT2"));
		}

		// SQLite only: the server admits or refuses the mapped type before any SQL is built, so no provider varies it.
		[Test]
		public void CatalogOnlyAssembly_AdmitsNoTypes([IncludeDataSources(ProviderName.SQLiteMS)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Types.Catalog", static s => s.SqlTextAssemblies.Add(_tests));

			Rejections(() => db.GetTable<RoutePerson>().Where(p => p.Id == RouteId.First).ToList())
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.TypeNotAllowed);
		}

		// SQLite only: as CatalogOnlyAssembly_AdmitsNoTypes.
		[Test]
		public void MappingAssembly_AdmitsItsTypes([IncludeDataSources(ProviderName.SQLiteMS)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Types.Mapping", static s => s.MappingAssemblies.Add(_tests));

			db
				.GetTable<RoutePerson>()
				.Where(p => p.Id == RouteId.First)
				.ToList()
				.Single()
				.Id
				.ShouldBe(RouteId.First);
		}

		static System.Collections.Generic.IReadOnlyList<RemoteRejection> Rejections(Action action)
		{
			var exception = Shouldly.Should.Throw<Exception>(action);

			for (Exception? e = exception; e != null; e = e.InnerException)
				if (e is RemoteRequestRejectedException rejected)
					return rejected.Rejections;

			throw new ShouldAssertException("Expected a RemoteRequestRejectedException, got: " + exception);
		}

		// An untrusted LinqService served in process, straight off the test database. RemoteDataContextBase caches server
		// information per configuration name, so each case uses its own.
		sealed class RouteContext : RemoteDataContextBase
		{
			readonly DataOptions         _backend;
			readonly string              _configuration;
			readonly Action<LinqService> _configure;

			public RouteContext(DataOptions backend, string configuration, Action<LinqService> configure)
				: base(new DataOptions().UseConfiguration(configuration))
			{
				_backend       = backend;
				_configuration = configuration;
				_configure     = configure;
			}

			protected override string ContextIDPrefix => _configuration;

			protected override ILinqService GetClient()
			{
				// AllowUpdates is a separate policy from the text check, so the DDL shapes reach the validator
				var service = new Service(_backend) { TrustedClient = false, AllowUpdates = true };

				service.AllowedConfigurations.Add(_configuration);
				service.MappingAssemblies.Add(_model);

				_configure(service);

				return service;
			}

			sealed class Service(DataOptions options) : LinqService
			{
				public override DataConnection CreateDataContext(string? configuration) => new(options);
			}
		}
	}
}
