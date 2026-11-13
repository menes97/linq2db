using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CodeGenerators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;

using NUnit.Framework;

using Shouldly;

namespace Tests.Analyzers.Internal
{
	/// <summary>
	/// Pins what the SQL text catalog generator emits for one site shape at a time. The generator runs for the linq2db
	/// assembly only, so each snippet is compiled under that name with its own copy of the attributes it keys on.
	/// </summary>
	[TestFixture]
	public class SqlTextCatalogGeneratorTests
	{
		const string Model = """
			namespace LinqToDB.Remote
			{
				enum SqlTextKind
				{
					Expression, Fragment, Function, TableExpression, Operator, QueryHint, ExtensionArgument,
					MergeHint, RawSqlTable, StatementHeader, StatementFooter, TableName, DbType,
				}

				[System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Property)]
				sealed class SqlTextAttribute : System.Attribute
				{
					public SqlTextAttribute(SqlTextKind kind) { }
				}
			}

			namespace LinqToDB
			{
				static class Sql
				{
					public class ExpressionAttribute : System.Attribute
					{
						public ExpressionAttribute(string? expression) { }
						public ExpressionAttribute(string configuration, string expression) { }
					}

					public class FunctionAttribute : System.Attribute
					{
						public FunctionAttribute() { }
						public FunctionAttribute(string name) { }
					}

					public class TableExpressionAttribute : System.Attribute
					{
						public TableExpressionAttribute() { }
						public TableExpressionAttribute(string expression) { }
					}

					public class TableFunctionAttribute : System.Attribute
					{
						public TableFunctionAttribute() { }
						public TableFunctionAttribute(string name) { }

						public string? Schema   { get; set; }
						public string? Database { get; set; }
					}

					public static T Expr<T>(System.FormattableString sql) => default!;
				}

				static class DataExtensions
				{
					public static object FromSql<T>(System.FormattableString sql) => null!;
				}
			}

			namespace Subject
			{
				using LinqToDB;
				using LinqToDB.Remote;

				sealed class Fragment
				{
					public Fragment([SqlText(SqlTextKind.Fragment)] string text) { }
				}

				sealed class Builder
				{
					[SqlText(SqlTextKind.Expression)]
					public string? Expression { get; set; }
				}

				sealed class SqlRawStatement
				{
					public SqlRawStatement(Fragment body) { }
				}

				// derives from a base attribute but builds its own text, so its member name is never sent
				sealed class XmlTableAttribute : Sql.TableExpressionAttribute
				{
				}

				// forwards its text to the base constructor under a parameter of its own naming, as SqlType's SqlExpression does
				sealed class TypeNameAttribute(string name) : Sql.ExpressionAttribute(name);

				static class Uses
				{
			{{body}}
				}
			}
			""";

		static readonly Regex _entry = new("""new\(SqlTextKind\.(?<kind>\w+), "(?<text>[^"\\]*)", (?:null|"(?<partition>[^"\\]*)"), """, RegexOptions.CultureInvariant);

		/// <summary>Generates the catalog for <paramref name="body"/> and returns its entries as <c>kind partition text</c>.</summary>
		static async Task<string[]> Generate(string body)
		{
			var references  = await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
			var compilation = CSharpCompilation.Create(
				"linq2db",
				[CSharpSyntaxTree.ParseText(Model.Replace("{{body}}", body), new CSharpParseOptions(LanguageVersion.Latest))],
				references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

			// a snippet that does not compile yields no operations, which would read as "nothing generated"
			compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

			var result = CSharpGeneratorDriver.Create(new SqlTextCatalogGenerator()).RunGenerators(compilation).GetRunResult();
			var source = result.GeneratedTrees.Single().ToString();

			return _entry.Matches(source)
				.Select(static m => $"{m.Groups["kind"].Value} {(m.Groups["partition"].Success ? m.Groups["partition"].Value : "-")} {m.Groups["text"].Value}")
				.ToArray();
		}

		static async Task ShouldGenerate(string body, params string[] expected)
		{
			(await Generate(body)).ShouldBe(expected, ignoreOrder: true);
		}

		// G1: a conditional append keeps the value before it, as the branch may not run
		[Test]
		public Task ConditionalAppendKeepsEarlierValue() => ShouldGenerate("""
					public static Fragment M(bool desc)
					{
						var text = "{0}";

						if (desc)
							text += " DESC";

						return new Fragment(text);
					}
			""",
			"Fragment - {0}",
			"Fragment - {0} DESC");

		[Test]
		public Task UnconditionalAppendReplacesEarlierValue() => ShouldGenerate("""
					public static Fragment M()
					{
						var text = "{0}";

						text += " DESC";

						return new Fragment(text);
					}
			""",
			"Fragment - {0} DESC");

		// G3: `??=` on a [SqlText] property catalogues the assigned value
		[Test]
		public Task CoalesceAssignmentToSqlTextPropertyIsCatalogued() => ShouldGenerate("""
					public static void M(Builder builder)
					{
						builder.Expression ??= "COALESCE({0}, 0)";
					}
			""",
			"Expression - COALESCE({0}, 0)");

		// G6: a readonly field also takes the values its constructors assign
		[Test]
		public Task ReadOnlyFieldTakesConstructorValues() => ShouldGenerate("""
					static readonly string Text = "{0}";

					static Uses()
					{
						Text = "{0} DESC";
					}

					public static Fragment M() => new Fragment(Text);
			""",
			"Fragment - {0}",
			"Fragment - {0} DESC");

		// G8: a raw statement is server-only, so its text is never catalogued; the same text elsewhere is
		[Test]
		public Task RawStatementTextIsNotCatalogued() => ShouldGenerate("""
					public static SqlRawStatement Ddl() => new SqlRawStatement(new Fragment("CREATE SEQUENCE {0}"));

					public static Fragment Query() => new Fragment("NEXT VALUE FOR {0}");
			""",
			"Fragment - NEXT VALUE FOR {0}");

		// G4: an attribute naming a configuration belongs to that provider family only
		[Test]
		public Task ConfigurationAttributeIsPartitioned() => ShouldGenerate("""
					[Sql.Expression("SQLite.MS", "printf('%d', {0})")]
					public static string Format(int value) => value.ToString();

					[Sql.Expression("MariaDB", "CONCAT({0})")]
					public static string Concat(int value) => value.ToString();
			""",
			"Expression SQLite printf('%d', {0})",
			"Expression MySql CONCAT({0})");

		// F5: a table function is rendered by its object name, checked part by part as TableName text
		[Test]
		public Task TableFunctionIsCataloguedAsTableName() => ShouldGenerate("""
					[Sql.TableFunction("GetRows", Schema = "dbo", Database = "Main")]
					public static object Rows() => null!;

					[Sql.TableFunction]
					public static object Unnamed() => null!;
			""",
			"TableName - GetRows",
			"TableName - dbo",
			"TableName - Main",
			"TableName - Unnamed");

		[Test]
		public Task DerivedAttributeTextUnderItsOwnParameterNameIsCatalogued() => ShouldGenerate("""
					[TypeName("nvarchar({0})")]
					public static string NVarChar(int size) => "";
			""",
			"Expression - nvarchar({0})");

		// the library's interpolated Sql.Expr and FromSql calls reach the server as their composite format
		[Test]
		public Task InterpolatedSqlIsCataloguedAsCompositeFormat() => ShouldGenerate("""
					public static bool Match(string column, string search) => Sql.Expr<bool>($"{column} MATCH {search}");

					public static object Table(string name) => DataExtensions.FromSql<int>($"SELECT * FROM {name}");

					public static string Braces(string value) => Sql.Expr<string>($"{{literal}} {value}");
			""",
			"Expression - {0} MATCH {1}",
			"RawSqlTable - SELECT * FROM {0}",
			"Expression - {{literal}} {0}");

		[Test]
		public Task InterpolationWithFormatIsNotCatalogued() => ShouldGenerate("""
					public static string Padded(int value) => Sql.Expr<string>($"{value,5}");
			""");

		// F6: the base attributes fall back to the member name; a derived one builds its own text
		[Test]
		public Task MemberNameFallbackIsForBaseAttributesOnly() => ShouldGenerate("""
					[Sql.Expression(null)]
					public static int Implicit(int value) => value;

					[Sql.Function]
					public static int Abs(int value) => value;

					[XmlTable]
					public static object XmlRows() => null!;
			""",
			"Expression - Implicit",
			"Function - Abs");
	}
}
