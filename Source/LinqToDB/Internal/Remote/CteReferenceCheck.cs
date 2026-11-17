using System.Collections.Generic;

using LinqToDB.Internal.SqlQuery;
using LinqToDB.Internal.SqlQuery.Visitors;

namespace LinqToDB.Internal.Remote
{
	/// <summary>
	/// Finds a CTE reference in a deserialized statement that its WITH clause does not declare in scope. The builder renders
	/// such a reference as the bare CTE name, which reads whatever table or view has that name.
	/// </summary>
	/// <remarks>
	/// A clause may reference the clauses declared before it. It may reference itself only when it is recursive, and then
	/// only from a set-operator branch of its body, not from the anchor. The statement's own query may reference any clause.
	/// </remarks>
	sealed class CteReferenceCheck : QueryElementVisitor
	{
		readonly List<CteClause> _clauses;

		int          _current = -1;
		bool         _selfAllowed;
		SqlCteTable? _invalid;

		CteReferenceCheck(List<CteClause> clauses) : base(VisitMode.ReadOnly)
		{
			_clauses = clauses;
		}

		/// <summary>Returns the first CTE reference of <paramref name="statement"/> outside the scope of its WITH clause.</summary>
		public static SqlCteTable? FindInvalidReference(SqlStatement statement)
		{
			var check = new CteReferenceCheck((statement as SqlStatementWithQueryBase)?.With?.Clauses ?? []);

			for (var i = 0; i < check._clauses.Count && check._invalid == null; i++)
			{
				check._current = i;
				check.Visit(check._clauses[i].Body);
			}

			check._current = -1;

			if (check._invalid == null)
				check.Visit(statement);

			return check._invalid;
		}

		// the clause bodies are walked one by one above, each with its own scope
		protected internal override IQueryElement VisitSqlWithClause(SqlWithClause element) => element;

		protected internal override IQueryElement VisitSqlSetOperator(SqlSetOperator element)
		{
			if (_current < 0 || _selfAllowed || !_clauses[_current].IsRecursive || _clauses[_current].Body?.SetOperators.Contains(element) != true)
				return base.VisitSqlSetOperator(element);

			_selfAllowed = true;

			try
			{
				return base.VisitSqlSetOperator(element);
			}
			finally
			{
				_selfAllowed = false;
			}
		}

		protected internal override IQueryElement VisitSqlCteTable(SqlCteTable element)
		{
			var index = element.Cte == null ? -1 : _clauses.IndexOf(element.Cte);

			var inScope = index >= 0 && (_current < 0 || index < _current || index == _current && _selfAllowed);

			if (!inScope)
				_invalid ??= element;

			return base.VisitSqlCteTable(element);
		}
	}
}
