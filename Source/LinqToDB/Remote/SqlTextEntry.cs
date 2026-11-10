namespace LinqToDB.Remote
{
	/// <summary>
	/// One entry of the effective SQL text catalog of a <see cref="LinqService"/>, as listed by <see cref="LinqService.DumpSqlText"/>.
	/// </summary>
	/// <param name="Kind">Position the text may be used at.</param>
	/// <param name="Text">SQL text, with numbered placeholders written as <c>{}</c>.</param>
	/// <param name="Source">Assembly that contributes the text, or <see cref="SqlTextCatalog.HostSource"/> for host edits.</param>
	/// <param name="Origin">Type that produces the text, when known.</param>
	/// <param name="State">Whether the text is accepted.</param>
	public sealed record SqlTextEntry(SqlTextKind Kind, string Text, string Source, string? Origin, SqlTextEntryState State);
}
