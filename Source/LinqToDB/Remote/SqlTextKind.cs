namespace LinqToDB.Remote
{
	/// <summary>
	/// Position in a remote request where SQL text is rendered. Text approved for one kind is not accepted for another.
	/// </summary>
	public enum SqlTextKind
	{
		/// <summary>SQL expression template.</summary>
		Expression,
		/// <summary>SQL fragment template.</summary>
		Fragment,
		/// <summary>Function name.</summary>
		Function,
		/// <summary>Table expression template.</summary>
		TableExpression,
		/// <summary>Binary operator.</summary>
		Operator,
		/// <summary>Query hint.</summary>
		QueryHint,
		/// <summary>Text argument of a query or table extension.</summary>
		ExtensionArgument,
		/// <summary>Hint of a merge statement.</summary>
		MergeHint,
		/// <summary>Text of a raw SQL table source.</summary>
		RawSqlTable,
		/// <summary>Header of a create table statement.</summary>
		StatementHeader,
		/// <summary>Footer of a create table statement.</summary>
		StatementFooter,
		/// <summary>Runtime override of a table, schema, database or server name.</summary>
		TableName,
	}
}
