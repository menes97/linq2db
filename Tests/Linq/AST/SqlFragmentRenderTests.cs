using System.Linq;

using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.ClickHouse;
using LinqToDB.DataProvider.MySql;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.DataProvider.SapHana;
using LinqToDB.DataProvider.SqlServer;
using LinqToDB.Mapping;

using NUnit.Framework;

using Shouldly;

namespace Tests.AST
{
	/// <summary>
	/// List-shaped SQL is built from nested fixed fragments so every text is an exact catalog entry for untrusted remote
	/// clients. Each test drives one production site end to end and pins the flat text the nesting must render. The
	/// connections are never opened: SQL generation needs only the provider and its mapping schema.
	/// </summary>
	[TestFixture]
	public class SqlFragmentRenderTests : TestBase
	{
		[Table("ListRow")]
		sealed class ListRow
		{
			[Column] public int     Id      { get; set; }
			[Column] public int     GroupId { get; set; }
			[Column] public int?    Value   { get; set; }
			[Column] public string? Name    { get; set; }
			[Column] public string? Note    { get; set; }
		}

		static string SqlOf<T>(IQueryable<T> query) => query.ToSqlQuery().Sql;

		static IQueryable<string?> OrderedAggregate(DataConnection db) =>
			db.GetTable<ListRow>()
				.GroupBy(r => r.GroupId)
				.Select(g => g.StringAggregate(", ", r => r.Name).OrderBy(r => r.Value).ThenByDescending(r => r.Id).ToValue());

		// StringMemberTranslatorBase.BuildAggregateNullsOrderBy; the second item nests NULLS FIRST around DESC
		[Test]
		public void AggregateOrderByWithNullsPosition()
		{
			using var db = new DataConnection(new DataOptions()
				.UsePostgreSQL("Server=offline", PostgreSQLVersion.v15)
				.UseDefaultNullsPosition(Sql.NullsPosition.First));

			SqlOf(OrderedAggregate(db)).ShouldContain("STRING_AGG(g_1.\"Name\", ', ' ORDER BY g_1.\"Value\" NULLS FIRST, g_1.\"Id\" DESC NULLS FIRST)");
		}

		// MySqlMemberTranslator: the NULLS emulation key ({0} IS NULL) is a list item of its own
		[Test]
		public void AggregateOrderByWithEmulatedNullsPosition()
		{
			using var db = new DataConnection(new DataOptions()
				.UseMySql("Server=offline", MySqlVersion.MySql80, MySqlProvider.MySqlConnector)
				.UseDefaultNullsPosition(Sql.NullsPosition.Last));

			SqlOf(OrderedAggregate(db)).ShouldContain("GROUP_CONCAT(`g_1`.`Name` ORDER BY (`g_1`.`Value` IS NULL), `g_1`.`Value`, `g_1`.`Id` DESC SEPARATOR ', ')");
		}

		// ClickHouseMemberTranslator: the aggregated tuple and the arraySort key tuple
		[Test]
		public void AggregateOrderByAsClickHouseTuples()
		{
			using var db = new DataConnection(new DataOptions().UseClickHouse("Host=offline", ClickHouseProvider.ClickHouseDriver));

			var sql = SqlOf(OrderedAggregate(db));

			sql.ShouldContain("arraySort((t) -> (if(isNull(t.1), 0, 1), t.1, if(isNull(t.2), 1, 0), negate(t.2)), groupArray((g_1.Value, g_1.Id, toString(g_1.Name))))");
		}

		// YdbMemberTranslator: the aggregated tuple and the ListSort key tuple
		[Test]
		public void AggregateOrderByAsYdbTuples()
		{
			using var db = new DataConnection(new DataOptions().UseYdb("Endpoint=grpc://offline:2136;Database=/local"));

			var sql = SqlOf(OrderedAggregate(db));

			sql.ShouldContain("ListSort(AGGREGATE_LIST((g_1.`Value`, g_1.Id, g_1.Name)), ($t) -> { return (if($t.0 IS NULL, 0, 1), $t.0, if($t.1 IS NULL, 1, 0), (-$t.1)) })");
		}

		// BasicSqlOptimizer: paging without OFFSET becomes ROW_NUMBER() OVER (ORDER BY {0})
		[Test]
		public void RowNumberPagingOrderBy()
		{
			using var db = new DataConnection(new DataOptions().UseSqlServer("Server=offline", SqlServerVersion.v2008, SqlServerProvider.MicrosoftDataSqlClient));

			var query = db.GetTable<ListRow>().OrderBy(r => r.Value).ThenByDescending(r => r.Id).Skip(1).Take(2);

			SqlOf(query).ShouldContain("ROW_NUMBER() OVER (ORDER BY [t1].[Value], [t1].[Id] DESC) as [RN]");
		}

		// Sql.FieldsExpr: the column list of a full-text table function
		[Test]
		public void FieldsExprColumnList()
		{
			using var db = new DataConnection(new DataOptions().UseSqlServer("Server=offline", SqlServerVersion.v2022, SqlServerProvider.MicrosoftDataSqlClient));

			var query = Sql.Ext.SqlServer().FreeTextTable<ListRow, int>(db.GetTable<ListRow>(), r => new { r.Name, r.Note }, "term");

			SqlOf(query).ShouldContain("FREETEXTTABLE([ListRow], ([Name], [Note]), @term)");
		}

		// SqlFn.PropertyBuilder: the property name is a VarChar literal, so it is catalogued as rendered
		[Test]
		public void PropertyNameRendersAsVarCharLiteral()
		{
			using var db = new DataConnection(new DataOptions().UseSqlServer("Server=offline", SqlServerVersion.v2022, SqlServerProvider.MicrosoftDataSqlClient));

			var query = db.GetTable<ListRow>().Select(r => SqlFn.DatabasePropertyEx(r.Name!, SqlFn.DatabasePropertyName.Collation));

			SqlOf(query).ShouldContain("DATABASEPROPERTYEX([r].[Name], 'Collation')");
		}

		// ClickHouseSqlExpressionConvertVisitor.OrNullAggregateName: a fixed name per aggregate
		[Test]
		public void StandardCompatibleAggregateName()
		{
			using var db = new DataConnection(new DataOptions().UseClickHouse("Host=offline", ClickHouseProvider.ClickHouseDriver, o => o with { UseStandardCompatibleAggregates = true }));

			var query = db.GetTable<ListRow>().GroupBy(r => r.GroupId).Select(g => g.Max(r => r.Value));

			SqlOf(query).ShouldContain("maxOrNull(g_1.Value)");
		}

		sealed class CalculationViews : DataConnection
		{
			public CalculationViews(DataOptions options) : base(options)
			{
			}

			[CalculationViewInputParametersExpression]
			public ITable<ListRow> ListView([SqlQueryDependent] int first, [SqlQueryDependent] string? second, [SqlQueryDependent] string third)
			{
				return this.TableFromExpression(() => ListView(first, second, third));
			}
		}

		// CalculationViewInputParametersExpressionAttribute: the PLACEHOLDER list skips null arguments
		[Test]
		public void CalculationViewInputParameterList()
		{
			using var db = new CalculationViews(new DataOptions().UseSapHana("Driver=offline", SapHanaProvider.ODBC));

			SqlOf(db.ListView(1, null, "x")).ShouldContain("('PLACEHOLDER' = ('$$first$$', '1', '$$third$$', 'x'))");
		}
	}
}
