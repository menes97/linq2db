using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using LinqToDB.Analyzers;
using LinqToDB.Remote;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using NUnit.Framework;

using Shouldly;

namespace Tests.Analyzers
{
	[TestFixture]
	[NonParallelizable]
	public sealed class SqlTextUserCatalogGeneratorTests
	{
		const string Usings = """
			using System;
			using System.Linq;
			using System.Linq.Expressions;

			using LinqToDB;
			using LinqToDB.Data;
			using LinqToDB.Mapping;

			""";

		static string Body(string statements) => Usings + """
			[Table] class Row { [Column] public int Id { get; set; } }

			static class Host
			{
				const string ConstHint = "CONST_HINT";

				static void M(IDataContext db, ITable<Row> table, string dynamicText, int id)
				{

			""" + statements + """

				}
			}
			""";

		#region Gate (TO-21a, TO-B6, TO-B10)

		[Test]
		public async Task NotOptedIn_GeneratesNothing()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""table.TableHint("NOLOCK").ToList();"""), enabled: false);

			result.GeneratedSource.ShouldBeNull();
			result.Diagnostics.ShouldBeEmpty();
		}

		[Test]
		public async Task OptedIn_GeneratesOneCompilingCatalog()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""table.TableHint("NOLOCK").ToList();"""));

			result.Run.GeneratedSources.Length.ShouldBe(1);
			result.Output.Errors().ShouldBeEmpty();
			result.Texts(SqlTextKind.ExtensionArgument).ShouldContain("NOLOCK");
		}

		[Test]
		public async Task NotOptedIn_DoesNotWalk()
		{
			var walked = 0;
			var hook   = typeof(SqlTextUserCatalogGenerator).GetProperty("OnWalk", BindingFlags.Static | BindingFlags.NonPublic)!;

			hook.SetValue(null, (Action<Compilation>)(_ => walked++));

			try
			{
				await GeneratorVerifier.RunAsync(Body("""table.TableHint("NOLOCK").ToList();"""), enabled: false);
				walked.ShouldBe(0);

				await GeneratorVerifier.RunAsync(Body("""table.TableHint("NOLOCK").ToList();"""));
				walked.ShouldBe(1);
			}
			finally
			{
				hook.SetValue(null, null);
			}
		}

		// A syntax provider runs its predicate over every tree of every project referencing the package, whatever the gate
		// says, and leaves no tracked step unless named, so the driver cannot observe it: guard the source instead.
		[Test]
		public void Generator_UsesNoSyntaxProvider()
		{
			var source = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "Source", "LinqToDB.Analyzers", "SqlTextUserCatalogGenerator.cs");
			var root   = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetRoot();

			root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Select(static m => m.Name.Identifier.Text)
				.ShouldNotContain("SyntaxProvider");
		}

		static string ThisFile([CallerFilePath] string path = "") => path;

		[Test]
		public async Task OptedIn_WithoutCatalogTypes_ReportsL2DB1009()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", withLinqToDB: false);

			result.GeneratedSource.ShouldBeNull();
			result.Diagnostics.ShouldContain(d => d.Id == "L2DB1009");
		}

		[Test]
		public async Task NotOptedIn_WithoutCatalogTypes_ReportsNothing()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", enabled: false, withLinqToDB: false);

			result.Diagnostics.ShouldBeEmpty();
		}

		#endregion

		#region Attributes (TO-21b)

		[Test]
		public async Task Attributes()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				namespace App.DataProvider.Reports
				{
					static class Functions
					{
						[Sql.Expression("USER_EXPR({0})")]                       public static int  Expr(int x)        => x;
						[Sql.Expression((string)null)]                            public static int  NamedExpr(int x)   => x;
						[Sql.Function("USER_FN")]                                 public static int  Fn(int x)          => x;
						[Sql.Function]                                            public static int  NamedFn(int x)     => x;
						[Sql.Function(Name = "NAMED_PROP_FN")]                    public static int  PropFn(int x)      => x;
						[Sql.Property("USER_PROP")]                               public static int  Prop               => 0;
						[Sql.Property]                                            public static int  NamedProp          => 0;
						[Sql.Extension("USER_EXT({value}{_}{sep?})")]             public static int  Ext(int value)     => value;
						[Sql.TableFunction("USER_TF", Schema = "TF_SCHEMA")]      public static ITable<Row> Tf()        => null;
						[Sql.TableExpression("{0} {1} USER_TE", Name = "TE_NAME")] public static ITable<Row> Te()       => null;
						[Derived]                                                 public static int  Derived(int x)     => x;
					}

					sealed class DerivedAttribute : Sql.ExpressionAttribute
					{
						public DerivedAttribute() : base((string)null) { }
					}

					[Table] class Row { [Column] public int Id { get; set; } }
				}
				""");

			result.Output.Errors().ShouldBeEmpty();

			result.Texts(SqlTextKind.Expression).ShouldBe(["NamedExpr", "NamedProp", "USER_EXPR({0})", "USER_EXT({value}{_}{sep?})", "USER_PROP"], ignoreOrder: true);
			result.Texts(SqlTextKind.Function).ShouldBe(["NAMED_PROP_FN", "NamedFn", "USER_FN"], ignoreOrder: true);
			result.Texts(SqlTextKind.TableExpression).ShouldBe(["{0} {1} USER_TE", "USER_TF"], ignoreOrder: true);
			result.Texts(SqlTextKind.TableName).ShouldBe(["USER_TF", "TF_SCHEMA", "TE_NAME"], ignoreOrder: true);

			result.Items.Single(i => i.Text == "USER_EXT({value}{_}{sep?})").IsTemplate.ShouldBeTrue();
			result.Items.Single(i => i.Text == "USER_EXPR({0})").IsTemplate.ShouldBeFalse();
			result.Items.ShouldAllBe(i => i.Partition == null);
			result.Items.Single(i => i.Text == "USER_FN").Origin.ShouldBe("App.DataProvider.Reports.Functions");
		}

		// TypedConstant.Value throws for an array argument; Tests/Linq's own mappings crashed the generator (CS8785) this way
		[Test]
		public async Task ArrayValuedAttributeArguments_AreSkipped()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				static class Functions
				{
					[Sql.TableFunction("TF_ARGS", ArgIndices = new[] { 0 })]             public static ITable<Row> Tf(int x)      => null;
					[Sql.TableExpression("{0} {1} TE_ARGS", ArgIndices = new[] { 0 })]  public static ITable<Row> Te(int x)      => null;
					[Sql.Function("FN_ARGS", 1, 0)]                                      public static int Fn(int a, int b)       => a;
					[Sql.Expression("EXPR_ARGS({0})", ArgIndices = new[] { 0 })]         public static int Expr(int a)            => a;
				}

				[Table] class Row { [Column] public int Id { get; set; } }
				""");

			result.Diagnostics.ShouldNotContain(static d => d.Id == "CS8785");
			result.Output.Errors().ShouldBeEmpty();
			result.Texts(SqlTextKind.TableExpression).ShouldBe(["TF_ARGS", "{0} {1} TE_ARGS"], ignoreOrder: true);
			result.Texts(SqlTextKind.Function).ShouldContain("FN_ARGS");
			result.Texts(SqlTextKind.Expression).ShouldContain("EXPR_ARGS({0})");
		}

		[Test]
		public async Task QueryExtensionArguments()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				static class Hints
				{
					[Sql.QueryExtension(null, Sql.QueryExtensionScope.TableHint, typeof(object), "ARG_ONE")]
					public static ITable<T> One<T>(this ITable<T> t) where T : notnull => t;

					[Sql.QueryExtension(Sql.QueryExtensionScope.TableHint, typeof(object), "ARG_A", "ARG_B")]
					public static ITable<T> Many<T>(this ITable<T> t) where T : notnull => t;
				}
				""");

			result.Output.Errors().ShouldBeEmpty();
			result.Texts(SqlTextKind.ExtensionArgument).ShouldBe(["ARG_A", "ARG_B", "ARG_ONE"], ignoreOrder: true);
		}

		[Test]
		public async Task FluentAttributeCreation()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				var a = new Sql.ExpressionAttribute("FLUENT_EXPR({0})");
				var b = new Sql.FunctionAttribute { Name = "FLUENT_FN" };
				var c = new Sql.ExpressionAttribute(dynamicText);
				"""));

			result.Texts(SqlTextKind.Expression).ShouldContain("FLUENT_EXPR({0})");
			result.Texts(SqlTextKind.Function).ShouldContain("FLUENT_FN");
			result.Diagnostics.Count(d => d.Id == "L2DB1005").ShouldBe(1);
		}

		[Test]
		public async Task AnnotatedLibraryParameters()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				sealed class Builder : Sql.IExtensionCallBuilder
				{
					public void Build(Sql.ISqlExtensionBuilder builder)
					{
						builder.Expression = "BUILT({0})";
						builder.AddFragment("part", "FRAGMENT_TEXT");
					}
				}
				""");

			result.Texts(SqlTextKind.Expression).ShouldContain("BUILT({0})");
			result.Texts(SqlTextKind.Fragment).ShouldContain("FRAGMENT_TEXT");
		}

		#endregion

		#region Quoted format items (TO-21e)

		[Test]
		public async Task QuotedFormatItem_IsRefused()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				static class Functions
				{
					[Sql.Expression("QUOTED('{0}')")]                    public static int Apostrophe(int x)   => x;
					[Sql.Expression("QUOTED(\"{0}\")")]                  public static int DoubleQuote(int x)  => x;
					[Sql.Expression("COMMENTED({0}) -- {1}")]            public static int Comment(int x)      => x;
					[Sql.Extension("NAMED('%{part}', {date})")]          public static int Named(int date)     => date;
					[Sql.Expression("ARRAY[{0}]")]                       public static int Array(int x)        => x;
					[Sql.Expression("QUOTED_CONSTANT('{{0}}', {0})")]    public static int Escaped(int x)      => x;
				}
				""");

			result.Diagnostics.Count(static d => d.Id == "L2DB1006").ShouldBe(4);
			result.Texts(SqlTextKind.Expression).ShouldBe(["ARRAY[{0}]", "QUOTED_CONSTANT('{{0}}', {0})"], ignoreOrder: true);
		}

		// A query hint is rendered on a line of its own, so a '--' comment hint ends there; inline kinds stay refused.
		[Test]
		public async Task CommentQueryHint_IsAccepted_InlineCommentIsRefused()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				db.QueryHints.Add("-- HINT COMMENT");
				db.QueryHints.Add("-- HINT {0}");
				table.WithTableExpression("{0} {1} -- TRAILING").ToList();
				"""));

			result.Texts(SqlTextKind.QueryHint).ShouldBe(["-- HINT COMMENT"]);
			result.Texts(SqlTextKind.TableExpression).ShouldBeEmpty();
			result.Diagnostics.Count(static d => d.Id == "L2DB1006").ShouldBe(2);
		}

		[Test]
		public async Task QuotedFormatItem_InAllowFile_IsRefused()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files:
			[
				new(GeneratorVerifier.ProjectDir + "/allow.txt", "+\tExpression\tALLOW_QUOTED('{0}')\n+\tExpression\tALLOW_PLAIN({0})"),
			]);

			result.Diagnostics.Single().Id.ShouldBe("L2DB1006");
			result.Texts(SqlTextKind.Expression).ShouldBe(["ALLOW_PLAIN({0})"]);
		}

		#endregion

		#region Call sites (TO-21c)

		[Test]
		public async Task RawSql()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				db.FromSql<Row>($"SELECT * FROM rows WHERE id = {id} AND x = {id,5} AND y = {id:N2} {{literal}}").ToList();
				db.FromSql<Row>("SELECT 1 FROM raw WHERE id = {0}", id).ToList();
				db.FromSqlScalar<int>($"SELECT {id}").ToList();
				table.Select(r => Sql.Expr<int>($"EXPR_FMT({r.Id})")).ToList();
				"""));

			result.Texts(SqlTextKind.RawSqlTable).ShouldBe(
				["SELECT * FROM rows WHERE id = {0} AND x = {1,5} AND y = {2:N2} {{literal}}", "SELECT 1 FROM raw WHERE id = {0}", "SELECT {0}"],
				ignoreOrder: true);
			result.Texts(SqlTextKind.Expression).ShouldContain("EXPR_FMT({0})");
			result.Diagnostics.ShouldNotContain(d => d.Id == "L2DB1005");
		}

		[Test]
		public async Task Hints()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				table.TableHint("TH").ToList();
				table.TableHint(ConstHint).ToList();
				table.With("WITH_HINT").ToList();
				table.QueryHint("QH_EXT").ToList();
				"""));

			result.Texts(SqlTextKind.ExtensionArgument).ShouldBe(["TH", "CONST_HINT", "WITH_HINT", "QH_EXT"], ignoreOrder: true);
			result.Texts(SqlTextKind.TableExpression).ShouldBe(["{0} {1} WITH (WITH_HINT)"]);
		}

		[Test]
		public async Task TableNamesAndStatements()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				table.TableName("TN").SchemaName("SN").DatabaseName("DN").ServerName("SV").ToList();
				table.WithTableExpression("{0} {1} TABLE_EXPR").ToList();
				db.Insert(new Row(), tableName: "INS_T");
				db.CreateTable<Row>(statementHeader: "HEADER {0}", statementFooter: "FOOTER");
				var o1 = new CreateTableOptions(TableName: "CTOR_T");
				var o2 = o1 with { SchemaName = "WITH_S", StatementFooter = "WITH_FOOTER" };
				var o3 = new CreateTempTableOptions { DatabaseName = "INIT_D" };
				var bulk = new BulkCopyOptions().WithTableName("BULK_T");
				var temp = new TempTable<Row>(db, tableName: "TEMP_T");
				"""));

			result.Texts(SqlTextKind.TableName).ShouldBe(
				["TN", "SN", "DN", "SV", "INS_T", "CTOR_T", "WITH_S", "INIT_D", "BULK_T", "TEMP_T"],
				ignoreOrder: true);
			result.Texts(SqlTextKind.TableExpression).ShouldContain("{0} {1} TABLE_EXPR");
			result.Texts(SqlTextKind.StatementHeader).ShouldBe(["HEADER {0}"]);
			result.Texts(SqlTextKind.StatementFooter).ShouldBe(["FOOTER", "WITH_FOOTER"], ignoreOrder: true);
		}

		[Test]
		public async Task MergeHint()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				table.Merge("MERGE_HINT");
				"""));

			result.Texts(SqlTextKind.MergeHint).ShouldBe(["MERGE_HINT"]);
		}

		[Test]
		public async Task QueryHintLists()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				db.QueryHints.Add("QH_ADD");
				db.NextQueryHints.Insert(0, "QH_INSERT");
				db.QueryHints.AddRange(new[] { "QH_RANGE_1", "QH_RANGE_2" });
				var alias = db.QueryHints;
				alias.Add("QH_ALIAS");
				using var dc = new DataConnection();
				dc.QueryHints.Add("QH_IMPL");
				"""));

			result.Texts(SqlTextKind.QueryHint).ShouldBe(["QH_ADD", "QH_INSERT", "QH_RANGE_1", "QH_RANGE_2", "QH_ALIAS", "QH_IMPL"], ignoreOrder: true);
		}

		#endregion

		#region Not constant (TO-21d)

		[Test]
		public async Task NotConstant_ReportsL2DB1005()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				table.TableHint(dynamicText).ToList();
				db.FromSql<Row>(dynamicText).ToList();
				db.QueryHints.Add(dynamicText);
				table.TableName(dynamicText).ToList();
				"""));

			result.Diagnostics.Count(d => d.Id == "L2DB1005").ShouldBe(4);
		}

		[Test]
		public async Task ConstantFlow_IsSilent()
		{
			var result = await GeneratorVerifier.RunAsync(Body("""
				var local = "LOCAL_HINT";
				table.TableHint(local).ToList();
				db.FromSql<Row>($"SELECT * FROM t WHERE x = {dynamicText}").ToList();
				"""));

			result.Diagnostics.ShouldNotContain(d => d.Id == "L2DB1005");
			result.Texts(SqlTextKind.ExtensionArgument).ShouldContain("LOCAL_HINT");
			result.Texts(SqlTextKind.RawSqlTable).ShouldContain("SELECT * FROM t WHERE x = {0}");
		}

		#endregion

		#region Files (TO-22)

		[Test]
		public async Task AllowFile_AddsEntryWithOrigin()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files:
			[
				new(GeneratorVerifier.ProjectDir + "/sql/allow.txt", "# comment\n+\tQueryHint\tALLOWED\\s\n+\tExpression\t\\e\n"),
			]);

			result.Diagnostics.ShouldBeEmpty();
			result.Items.Single(i => i.Text == "ALLOWED ").Origin.ShouldBe("sql/allow.txt");
			result.Texts(SqlTextKind.Expression).ShouldContain("");
		}

		[Test]
		public async Task AllowFile_NotMarked_IsIgnored()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files:
			[
				new(GeneratorVerifier.ProjectDir + "/other.txt", "+\tQueryHint\tNOT_A_CATALOG", IsCatalogFile: false),
			]);

			result.Items.ShouldNotContain(i => i.Text == "NOT_A_CATALOG");
		}

		[TestCase("+\tQueryHint")]
		[TestCase("*\tQueryHint\tX")]
		[TestCase("+\tNoSuchKind\tX")]
		[TestCase("+\tQueryHint\tX\\q")]
		[TestCase("+\tQueryHint\tX\\")]
		public async Task AllowFile_MalformedLine_ReportsL2DB1007(string line)
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files: [new(GeneratorVerifier.ProjectDir + "/allow.txt", line)]);

			result.Diagnostics.Single().Id.ShouldBe("L2DB1007");
			result.Items.ShouldBeEmpty();
		}

		[TestCase("/proj2/allow.txt",           null)]
		[TestCase("/proj/../outside/allow.txt", null)]
		[TestCase("/proj/obj/allow.txt",        null)]
		[TestCase("obj/allow.txt",              null)]
		[TestCase("/shared/allow.txt",          null)]
		public async Task AllowFile_OutsideProject_ReportsL2DB1008(string path, string? roots)
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files: [new(path, "+\tQueryHint\tOUTSIDE")], roots: roots);

			result.Diagnostics.Single().Id.ShouldBe("L2DB1008");
			result.Items.ShouldNotContain(i => i.Text == "OUTSIDE");
		}

		[TestCase("sql/allow.txt",         null)]
		[TestCase("/proj/./sql/allow.txt", null)]
		[TestCase("/shared/allow.txt",     "/shared")]
		[TestCase("/shared/allow.txt",     "/other;/shared/")]
		public async Task AllowFile_InsideProjectOrRoot_IsRead(string path, string? roots)
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files: [new(path, "+\tQueryHint\tINSIDE")], roots: roots);

			result.Diagnostics.ShouldBeEmpty();
			result.Texts(SqlTextKind.QueryHint).ShouldContain("INSIDE");
		}

		[Test]
		public async Task DenyLine_EmitsRemoval()
		{
			var result = await GeneratorVerifier.RunAsync("class C { }", files:
			[
				new(GeneratorVerifier.ProjectDir + "/deny.txt", "-\tFunction\tSUBSTRING\n-\tQueryHint\tDENIED_QUOTED('{0}')\n+\tQueryHint\tALSO_ALLOWED"),
			]);

			result.Diagnostics.ShouldBeEmpty();
			result.Output.Errors().ShouldBeEmpty();

			// a removal only narrows, so even a text the quoting rule would refuse as an addition is carried
			result.Items.Where(static i => i.IsRemoval).Select(static i => (i.Kind, i.Text)).ShouldBe(
				[(SqlTextKind.Function, "SUBSTRING"), (SqlTextKind.QueryHint, "DENIED_QUOTED('{0}')")],
				ignoreOrder: true);
			result.Items.Single(static i => i.IsRemoval && i.Text == "SUBSTRING").Origin.ShouldBe("deny.txt");
			result.Items.Single(static i => i.Text == "ALSO_ALLOWED").IsRemoval.ShouldBeFalse();
		}

		// The catalog contract of a linq2db that predates SqlTextItem.IsRemoval, declared in source.
		const string CatalogWithoutRemovals = """
			namespace LinqToDB.Remote
			{
				public enum SqlTextKind { Expression, QueryHint }
				public interface ISqlTextCatalog { System.Collections.Generic.IReadOnlyList<SqlTextItem> Items { get; } }
				[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
				public sealed class SqlTextCatalogAttribute : System.Attribute { public SqlTextCatalogAttribute(System.Type type) { } }
				public sealed class SqlTextItem { public SqlTextItem(SqlTextKind kind, string text, string partition, string origin, bool isTemplate) { } }
			}
			""";

		[Test]
		public async Task DenyLine_WithoutRemovalSupport_ReportsL2DB1009()
		{
			var result = await GeneratorVerifier.RunAsync(CatalogWithoutRemovals, withLinqToDB: false, files:
			[
				new(GeneratorVerifier.ProjectDir + "/deny.txt", "-\tQueryHint\tDENIED\n+\tQueryHint\tALLOWED"),
			]);

			result.Diagnostics.Single().Id.ShouldBe("L2DB1009");
			result.Output.Errors().ShouldBeEmpty();
			result.GeneratedSource!.ShouldContain("\"ALLOWED\"");
			result.GeneratedSource!.ShouldNotContain("\"DENIED\"");
		}

		[Test]
		public async Task DbType()
		{
			var result = await GeneratorVerifier.RunAsync(Usings + """
				[Table]
				class Row
				{
					[Column(DbType = "COLUMN_DBTYPE(10)")] public string A { get; set; }
					[DataType("DATATYPE_DBTYPE")]          public string B { get; set; }
					[Column]                               public string C { get; set; }
				}

				static class Host
				{
					static void M(FluentMappingBuilder builder)
					{
						builder.Entity<Row>().Property(r => r.C).HasDbType("FLUENT_DBTYPE");
						var t = new DbDataType(typeof(string), "CTOR_DBTYPE").WithDbType("WITH_DBTYPE");
					}
				}
				""");

			result.Output.Errors().ShouldBeEmpty();
			result.Texts(SqlTextKind.DbType).ShouldBe(
				["COLUMN_DBTYPE(10)", "DATATYPE_DBTYPE", "FLUENT_DBTYPE", "CTOR_DBTYPE", "WITH_DBTYPE"],
				ignoreOrder: true);
		}

		#endregion

		#region Emitted code (TO-B5)

		[Test]
		public async Task EmittedCode_CompilesAsCSharp73()
		{
			var result = await GeneratorVerifier.RunAsync("class C { [LinqToDB.Sql.Function(\"FN_73\")] static int F() => 0; }", languageVersion: LanguageVersion.CSharp7_3);

			result.Output.Errors().ShouldBeEmpty();
			result.Texts(SqlTextKind.Function).ShouldContain("FN_73");
		}

		[Test]
		public async Task TwoOptedInAssemblies_WithInternalsVisibleTo_DoNotCollide()
		{
			var first = await GeneratorVerifier.RunAsync(
				"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Second\")] class A { [LinqToDB.Sql.Function(\"FN_A\")] static int F() => 0; }",
				assemblyName: "First");

			first.Output.Errors().ShouldBeEmpty();

			var second = await GeneratorVerifier.RunAsync(
				"class B { [LinqToDB.Sql.Function(\"FN_B\")] static int F() => 0; }",
				assemblyName: "Second",
				extraReferences: [first.Output.ToMetadataReference()]);

			second.Output.GetDiagnostics().ShouldNotContain(d => d.Id == "CS0436");
			second.Output.Errors().ShouldBeEmpty();
		}

		#endregion
	}
}
