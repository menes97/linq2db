using System;
using System.Collections.Generic;

namespace LinqToDB.Internal.SqlQuery
{
	/// <summary>
	/// Builds list-shaped SQL from nested fixed fragments instead of a generated format string, so every text an
	/// untrusted remote client sends is an exact catalog entry. Nested fragments render the same text as a flat list.
	/// </summary>
	static class SqlFragmentList
	{
		/// <summary>Renders <c>item0, item1, …</c>.</summary>
		public static ISqlExpression Join(IReadOnlyList<ISqlExpression> items)
		{
			if (items.Count == 0)
				throw new ArgumentException("List must have at least one item.", nameof(items));

			var result = items[items.Count - 1];

			for (var i = items.Count - 2; i >= 0; i--)
				result = new SqlFragment("{0}, {1}", items[i], result);

			return result;
		}

		/// <summary>Renders <c>(item0, item1, …)</c>.</summary>
		public static ISqlExpression Tuple(IReadOnlyList<ISqlExpression> items)
		{
			return new SqlFragment("({0})", Join(items));
		}

		/// <summary>Renders an ordering item: <c>expr [DESC]</c>.</summary>
		public static ISqlExpression OrderItem(ISqlExpression expr, bool desc)
		{
			return desc ? new SqlFragment("{0} DESC", expr) : expr;
		}

		/// <summary>Renders an ordering item: <c>expr [DESC] NULLS FIRST|LAST</c>.</summary>
		public static ISqlExpression OrderItem(ISqlExpression expr, bool desc, bool nullsFirst)
		{
			var item = OrderItem(expr, desc);

			return nullsFirst
				? new SqlFragment("{0} NULLS FIRST", item)
				: new SqlFragment("{0} NULLS LAST", item);
		}
	}
}
