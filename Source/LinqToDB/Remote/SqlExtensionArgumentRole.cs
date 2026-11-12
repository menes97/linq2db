namespace LinqToDB.Remote
{
	/// <summary>
	/// How an extension builder renders the value of one of its argument keys. A <see cref="LinqService"/> checks the value an
	/// untrusted client sends for the key by this role.
	/// </summary>
	public enum SqlExtensionArgumentRole
	{
		/// <summary>The builder never renders the value; any value is accepted.</summary>
		Unrendered,
		/// <summary>A string the builder writes as is: accepted only as <see cref="SqlTextKind.ExtensionArgument"/> catalog text.</summary>
		Text,
		/// <summary>An <see cref="int"/> count of indexed keys.</summary>
		Count,
		/// <summary>A value the builder writes as is: a string of ASCII letters, digits and underscores, or a number.</summary>
		Identifier,
		/// <summary>As <see cref="Identifier"/>, or a <see cref="Sql.SqlID"/> the builder resolves through <c>ISqlBuilder.BuildSqlID</c>.</summary>
		IdentifierOrSqlId,
		/// <summary>A <see cref="Sql.SqlID"/> the builder resolves through <c>ISqlBuilder.BuildSqlID</c>.</summary>
		SqlId,
		/// <summary>A query element the builder renders through <c>ISqlBuilder.BuildExpression</c>; it is validated as an element.</summary>
		Element,
	}
}
