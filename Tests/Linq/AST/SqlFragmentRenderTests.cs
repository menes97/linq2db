using System;
using System.Linq;
using System.Text;

using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.ClickHouse;
using LinqToDB.DataProvider.MySql;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.DataProvider.SapHana;
using LinqToDB.DataProvider.SqlServer;
using LinqToDB.Internal.DataProvider;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.SqlQuery;

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

#if !NETFRAMEWORK
		// YdbMemberTranslator: the aggregated tuple and the ListSort key tuple (no YDB driver on .NET Framework)
		[Test]
		public void AggregateOrderByAsYdbTuples()
		{
			using var db = new DataConnection(new DataOptions().UseYdb("Endpoint=grpc://offline:2136;Database=/local"));

			var sql = SqlOf(OrderedAggregate(db));

			sql.ShouldContain("ListSort(AGGREGATE_LIST((g_1.`Value`, g_1.Id, g_1.Name)), ($t) -> { return (if($t.0 IS NULL, 0, 1), $t.0, if($t.1 IS NULL, 1, 0), (-$t.1)) })");
		}
#endif

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

		// renders a hand-built select the way the server does for a deserialized request: the convert visitor, then the builder
		static string RenderSelect(DataConnection db, params ISqlExpression[] columns)
		{
			var query = new SelectQuery();

			foreach (var column in columns)
				query.Select.Add(column);

			var provider     = db.DataProvider;
			var sqlOptimizer = provider.GetSqlOptimizer(db.Options);
			var factory      = sqlOptimizer.CreateSqlExpressionFactory(provider.MappingSchema, db.Options);

			var optimizationContext = new OptimizationContext(
				new EvaluationContext(),
				db.Options,
				provider.SqlProviderFlags,
				provider.MappingSchema,
				new SqlExpressionOptimizerVisitor(false),
				sqlOptimizer.CreateConvertVisitor(false),
				factory,
				false,
				static () => NoopQueryParametersNormalizer.Instance);

			var sb = new StringBuilder();

			provider
				.CreateSqlBuilder(provider.MappingSchema, db.Options)
				.BuildSql(new SqlSelectStatement(query), sb, optimizationContext, new AliasesContext(), null, 0);

			return sb.ToString();
		}

		static DataConnection OfflineConnection(string provider) => provider switch
		{
			"SqlServer"  => new DataConnection(new DataOptions().UseSqlServer("Server=offline", SqlServerVersion.v2022, SqlServerProvider.MicrosoftDataSqlClient)),
			"PostgreSQL" => new DataConnection(new DataOptions().UsePostgreSQL("Server=offline", PostgreSQLVersion.v15)),
			"MySql"      => new DataConnection(new DataOptions().UseMySql("Server=offline", MySqlVersion.MySql80, MySqlProvider.MySqlConnector)),
			"SQLite"     => new DataConnection(new DataOptions().UseSQLite("Data Source=offline")),
			"ClickHouse" => new DataConnection(new DataOptions().UseClickHouse("Host=offline", ClickHouseProvider.ClickHouseDriver)),
			"DuckDB"     => new DataConnection(new DataOptions().UseDuckDB("Data Source=offline")),
			"Ydb"        => new DataConnection(new DataOptions().UseYdb("Endpoint=grpc://offline:2136;Database=/local")),
			_            => throw new ArgumentOutOfRangeException(nameof(provider)),
		};

		static SqlUnaryExpression Negate(SqlValue value) =>
			new(value.ValueType, value, SqlUnaryOperation.Negation, Precedence.Unary);

		// BasicSqlBuilder writes a minus sign before its operand: an operand that renders with a leading minus must not
		// join it into a line comment
		[Test]
		public void MinusBeforeNegativeOperandIsSeparated([Values("SqlServer", "PostgreSQL", "MySql", "SQLite")] string provider)
		{
			using var db = OfflineConnection(provider);

			var sql = RenderSelect(
				db,
				Negate(new SqlValue(-1)),
				Negate(new SqlValue(-1.5m)),
				new SqlBinaryExpression(typeof(int), new SqlValue(-1), "*", new SqlValue(-5), Precedence.Multiplicative));

			sql.ShouldNotContain("--");
			sql.ShouldContain("- -1,");
			sql.ShouldContain("- -1.5,");
			sql.ShouldContain("- -5");
		}

		// a template's own minus sign before a value that renders with a leading minus (FromSql($"a-{x}"), Sql.Expr)
		[Test]
		public void TemplateMinusBeforeNegativeValueIsSeparated([Values("SqlServer", "PostgreSQL", "SQLite", "ClickHouse")] string provider)
		{
			using var db = OfflineConnection(provider);

			var sql = RenderSelect(
				db,
				new SqlExpression(new DbDataType(typeof(int)), "LIMIT_A-{0}", Precedence.Primary, new SqlValue(-5)),
				new SqlExpression(new DbDataType(typeof(int)), "LIMIT_B-{0}", Precedence.Primary, new SqlValue(5)));

			sql.ShouldNotContain("--");
			sql.ShouldContain("LIMIT_A- -5");
			sql.ShouldContain("LIMIT_B-5");
		}

		// Sql.Collate writes the name unquoted on most providers, so it must be a single token
		[Test]
		public void CollateRefusesNameThatIsNotOneToken([Values("NOCASE-1", "-NOCASE", "NOCASE@p", "NOCASE\n", "NOCASE /*", "")] string collation)
		{
			using var db = OfflineConnection("SQLite");

			Assert.That(() => db.GetTable<ListRow>().Where(r => r.Name.Collate(collation) == "x").ToSqlQuery(), Throws.TypeOf<InvalidOperationException>());
		}

		[Test]
		public void CollateAcceptsOneTokenName([Values("NOCASE", "nocase.noaccent", "Latin1_General_CI_AS")] string collation)
		{
			using var db = OfflineConnection("SQLite");

			SqlOf(db.GetTable<ListRow>().Select(r => r.Name.Collate(collation))).ShouldContain($"COLLATE {collation}");
		}

		// PostgreSQL quotes the name; its braces must not act as format items
		[Test]
		public void PostgreSQLCollateNameBracesAreLiteral()
		{
			using var db = OfflineConnection("PostgreSQL");

			SqlOf(db.GetTable<ListRow>().Select(r => r.Name.Collate("MARK{0}{{"))).ShouldContain("COLLATE \"MARK{0}{{\"");
		}

		// SqlFn.Collate (SQL Server) validates the name as Sql.Collate does
		[Test]
		public void SqlServerFnCollateRefusesNameThatIsNotOneToken()
		{
			using var db = OfflineConnection("SqlServer");

			Assert.That(() => db.GetTable<ListRow>().Select(r => SqlFn.Collate(r.Name, "Latin1_General_CI_AS--")).ToSqlQuery(), Throws.TypeOf<InvalidOperationException>());
		}

		// a backslash is an escape in a PostgreSQL '...' literal when standard_conforming_strings is off
		[Test]
		public void PostgreSQLBackslashLiteralReadsTheSameInBothStringModes()
		{
			using var db = OfflineConnection("PostgreSQL");

			var sql = RenderSelect(
				db,
				new SqlValue(typeof(string), "MARK\\' X"),
				new SqlValue(typeof(char),   '\\'),
				new SqlValue(typeof(string), "plain"),
				new SqlValue(typeof(string), "a'b"));

			sql.ShouldContain("E'MARK\\\\'' X'");
			sql.ShouldContain("E'\\\\'");
			sql.ShouldContain("'plain'");
			sql.ShouldContain("'a''b'");
		}

		// SAP HANA writes a HanaDecimal's own text, which is accepted only as a number (the converter needs the driver, the check does not)
		[TestCase("0",                     true)]
		[TestCase("10.5",                  true)]
		[TestCase(".5",                    true)]
		[TestCase("+1.",                   true)]
		[TestCase("-9.999999999999999E348", true)]
		[TestCase("1e-5",                  true)]
		[TestCase("MARK",                  false)]
		[TestCase("1) OR (1",              false)]
		[TestCase("1--2",                  false)]
		[TestCase("1'",                    false)]
		[TestCase(".",                     false)]
		[TestCase("1e",                    false)]
		[TestCase("",                      false)]
		public void HanaDecimalTextIsANumber(string text, bool isNumber)
		{
			var check = typeof(LinqToDB.Internal.DataProvider.SapHana.SapHanaProviderAdapter).GetMethod("IsNumericLiteral", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

			((bool)check.Invoke(null, [text])!).ShouldBe(isNumber);
		}

		// SqlString and SqlChars are written as the provider writes a string: ClickHouse, MySQL and YDB escape the backslash
		[Test]
		public void SqlStringIsWrittenAsTheProviderWritesAString([Values("ClickHouse", "MySql", "PostgreSQL", "SQLite")] string provider)
		{
			using var db = OfflineConnection(provider);

			const string text = "MARK\\' X";

			var asString = RenderSelect(db, new SqlValue(typeof(string), text));

			RenderSelect(db, new SqlValue(typeof(System.Data.SqlTypes.SqlString), new System.Data.SqlTypes.SqlString(text))).ShouldBe(asString);
			RenderSelect(db, new SqlValue(typeof(System.Data.SqlTypes.SqlChars),  new System.Data.SqlTypes.SqlChars(text))).ShouldBe(asString);
		}

#if !NETFRAMEWORK
		[Test]
		public void YdbSqlStringIsWrittenAsTheProviderWritesAString()
		{
			using var db = OfflineConnection("Ydb");

			const string text = "MARK\\' X";

			RenderSelect(db, new SqlValue(typeof(System.Data.SqlTypes.SqlString), new System.Data.SqlTypes.SqlString(text))).ShouldBe(RenderSelect(db, new SqlValue(typeof(string), text)));
		}
#endif

		// Oracle and Sybase keep the command text ASCII: an 8-bit connection charset maps U+FF07 / U+2019 to an apostrophe
		[Test]
		public void NonAsciiLiteralIsWrittenAsAscii([Values("Oracle", "Sybase")] string provider)
		{
			using var db = provider == "Oracle"
				? new DataConnection(new DataOptions().UseOracle("Data Source=offline", LinqToDB.DataProvider.Oracle.OracleVersion.v12, LinqToDB.DataProvider.Oracle.OracleProvider.Managed))
				: new DataConnection(new DataOptions().UseAse("Data Source=offline", LinqToDB.DataProvider.Sybase.SybaseProvider.DataAction));

			var sql = RenderSelect(
				db,
				new SqlValue(typeof(string), "MARK ÄΩ＇’ \\ ' \U0001F600"),
				new SqlValue(new DbDataType(typeof(string), DataType.NVarChar), "MARK＇"),
				new SqlValue(typeof(char), '＇'),
				new SqlValue(typeof(string), "plain 'ascii'"));

			sql.ShouldAllBe(c => c < 0x80);
			sql.ShouldContain("'plain ''ascii'''");
		}

		// the control: an operand without a leading minus keeps the minus sign beside it
		[Test]
		public void MinusBeforeOperandStaysAttached()
		{
			using var db = OfflineConnection("SQLite");

			var sql = RenderSelect(
				db,
				Negate(new SqlValue(1)),
				new SqlBinaryExpression(typeof(int), new SqlValue(-1), "*", new SqlValue(5), Precedence.Multiplicative));

			sql.ShouldContain("-1,");
			sql.ShouldContain("-5");
			sql.ShouldNotContain("- ");
		}

#if !NETFRAMEWORK
		// DuckDB writes a BitArray string as a BITSTRING literal (no DuckDB or YDB driver on .NET Framework)
		[Test]
		public void DuckDBBitStringKeepsTheValueInsideTheLiteral()
		{
			using var db = OfflineConnection("DuckDB");

			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.BitArray), "0101")).ShouldContain("'0101'::BITSTRING");
			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.BitArray), "01'01")).ShouldContain("'01''01'::BITSTRING");
		}

		// YDB writes a Decimal / DecFloat string through Decimal('...', p, s) / DyNumber('...')
		[Test]
		public void YdbNumericStringKeepsTheValueInsideTheLiteral()
		{
			using var db = OfflineConnection("Ydb");

			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.Decimal, null, null, 10, 2), "1.5")).ShouldContain("Decimal('1.5', 10, 2)");
			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.Decimal, null, null, 10, 2), "1'5")).ShouldContain("Decimal('1\\'5', 10, 2)");
			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.DecFloat), "1.5")).ShouldContain("DyNumber('1.5')");
			RenderSelect(db, new SqlValue(new DbDataType(typeof(string), DataType.DecFloat), "1'5")).ShouldContain("DyNumber('1\\'5')");
		}
#endif

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
