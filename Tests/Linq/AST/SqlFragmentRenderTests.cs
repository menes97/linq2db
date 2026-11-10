using System.Text;

using LinqToDB;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.SqlQuery;

using NUnit.Framework;

using Shouldly;

namespace Tests.AST
{
	/// <summary>
	/// List-shaped SQL is built from nested fixed fragments so every text is an exact catalog entry for untrusted remote
	/// clients. These tests pin that the nesting renders the same text as the flat format string it replaced.
	/// </summary>
	[TestFixture]
	public class SqlFragmentRenderTests : TestBase
	{
		static string Render(IDataContext db, ISqlExpression expr)
		{
			var sb = new StringBuilder();
			((ISqlBuilder)db.CreateSqlBuilder()).BuildExpression(sb, expr, false);
			return sb.ToString();
		}

		static ISqlExpression[] Items() =>
		[
			new SqlValue(1),
			new SqlBinaryExpression(typeof(int), new SqlValue(2), "+", new SqlValue(3), Precedence.Additive),
			new SqlFunction(new DbDataType(typeof(int)), "ABS", new SqlValue(4)),
		];

		static ISqlExpression List(params ISqlExpression[] items)
		{
			var result = items[items.Length - 1];

			for (var i = items.Length - 2; i >= 0; i--)
				result = new SqlFragment("{0}, {1}", items[i], result);

			return result;
		}

		[Test]
		public void OrderByListMatchesFlatText([DataSources(false)] string context)
		{
			using var db = GetDataContext(context);

			var i = Items();

			var flat   = new SqlFragment("ORDER BY {0}, {1} DESC NULLS FIRST, {2} NULLS LAST", i[0], i[1], i[2]);
			var nested = new SqlFragment("ORDER BY {0}", List(
				i[0],
				new SqlFragment("{0} NULLS FIRST", new SqlFragment("{0} DESC", i[1])),
				new SqlFragment("{0} NULLS LAST", i[2])));

			// the lower-precedence item must be parenthesised in both forms, or the comparison proves nothing about nesting
			Render(db, flat).ShouldContain("(2 + 3) DESC");
			Render(db, nested).ShouldBe(Render(db, flat));
		}

		[Test]
		public void TupleMatchesFlatText([DataSources(false)] string context)
		{
			using var db = GetDataContext(context);

			var i = Items();

			var flat   = new SqlFragment("({0}, {1}, {2})", i[0], i[1], i[2]);
			var nested = new SqlFragment("({0})", List(i[0], i[1], i[2]));

			Render(db, nested).ShouldBe(Render(db, flat));
		}

		[Test]
		public void SingleItemListMatchesFlatText([DataSources(false)] string context)
		{
			using var db = GetDataContext(context);

			var i = Items();

			var flat   = new SqlFragment("ORDER BY {0} DESC", i[1]);
			var nested = new SqlFragment("ORDER BY {0}", new SqlFragment("{0} DESC", i[1]));

			Render(db, nested).ShouldBe(Render(db, flat));
		}

		[Test]
		public void RowNumberOrderByMatchesFlatText([DataSources(false)] string context)
		{
			using var db = GetDataContext(context);

			var i    = Items();
			var type = db.MappingSchema.GetDbDataType(typeof(long));

			var flat   = new SqlExpression(type, "ROW_NUMBER() OVER (ORDER BY {0}, {1} DESC, {2})", Precedence.Primary, SqlFlags.IsWindowFunction, ParametersNullabilityType.NotNullable, i[0], i[1], i[2]);
			var nested = new SqlExpression(type, "ROW_NUMBER() OVER (ORDER BY {0})", Precedence.Primary, SqlFlags.IsWindowFunction, ParametersNullabilityType.NotNullable,
				List(i[0], new SqlFragment("{0} DESC", i[1]), i[2]));

			Render(db, nested).ShouldBe(Render(db, flat));
		}
	}
}
