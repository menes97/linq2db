using System;
using System.IO;
using System.Linq;
using System.Reflection;

using LinqToDB;
using LinqToDB.Data;
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
			Expression,
			TableExpression,
			SchemaName,
		}

		[Sql.Expression("({0} + 17)", ServerSideOnly = true)]
		static int Plus17(int value) => throw new InvalidOperationException();

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
		static void Run(IDataContext db, Shape shape)
		{
			var id = 1;

			switch (shape)
			{
				case Shape.RawSql:
					db.FromSql<Person>($"SELECT * FROM Person WHERE PersonID = {id} AND 17 = 17").ToList().Count.ShouldBe(1);
					break;
				case Shape.Expression:
					db.GetTable<Person>().Where(p => p.ID == id).Select(p => Plus17(p.ID)).ToList().ShouldBe(new[] { 18 });
					break;
				case Shape.TableExpression:
					db.GetTable<Person>().WithTableExpression("{0} {1} NOT INDEXED").Where(p => p.ID == id).ToList().Count.ShouldBe(1);
					break;
				case Shape.SchemaName:
					db.GetTable<Person>().SchemaName("main").Where(p => p.ID == id).ToList().Count.ShouldBe(1);
					break;
			}
		}

		static SqlTextKind KindOf(Shape shape) => shape switch
		{
			Shape.RawSql          => SqlTextKind.RawSqlTable,
			Shape.Expression      => SqlTextKind.Expression,
			Shape.TableExpression => SqlTextKind.TableExpression,
			Shape.SchemaName      => SqlTextKind.TableName,
			_                     => throw new ArgumentOutOfRangeException(nameof(shape)),
		};

		[Test]
		public void CatalogOnlyAssembly_AcceptsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Assemblies." + shape, static s => s.SqlTextAssemblies.Add(_tests));

			Run(db, shape);
		}

		[Test]
		public void WithoutCatalog_RejectsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.None." + shape, static _ => { });

			var rejection = Rejection(() => Run(db, shape));

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
			rejection.Kind.ShouldBe(KindOf(shape));
		}

		[Test]
		public void ExportedCatalog_LoadedByTheHost_AcceptsItsText([IncludeDataSources(ProviderName.SQLiteMS)] string context, [Values] Shape shape)
		{
			using var writer = new StringWriter();

			SqlTextCatalog.Export(writer, _tests);

			var exported = writer.ToString();

			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Export." + shape, s => s.SqlText.Load(new StringReader(exported)));

			Run(db, shape);
		}

		[Test]
		public void CatalogOnlyAssembly_AdmitsNoTypes([IncludeDataSources(ProviderName.SQLiteMS)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Types.Catalog", static s => s.SqlTextAssemblies.Add(_tests));

			Rejections(() => db.GetTable<RoutePerson>().Where(p => p.Id == RouteId.First).ToList())
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.TypeNotAllowed);
		}

		[Test]
		public void MappingAssembly_AdmitsItsTypes([IncludeDataSources(ProviderName.SQLiteMS)] string context)
		{
			using var backend = GetDataConnection(context);
			using var db      = new RouteContext(backend.Options, "SqlTextRoute.Types.Mapping", static s => s.MappingAssemblies.Add(_tests));

			db.GetTable<RoutePerson>().Where(p => p.Id == RouteId.First).ToList().Single().Id.ShouldBe(RouteId.First);
		}

		static RemoteRejection Rejection(Action action) => Rejections(action).ShouldHaveSingleItem();

		static System.Collections.Generic.IReadOnlyList<RemoteRejection> Rejections(Action action)
		{
			var exception = Assert.Catch<Exception>(() => action());

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
				var service = new Service(_backend) { TrustedClient = false };

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
