namespace LinqToDB.Remote
{
	/// <summary>
	/// One text accepted from untrusted clients.
	/// </summary>
	/// <param name="Kind">Position the text may be used at.</param>
	/// <param name="Text">SQL text.</param>
	/// <param name="Partition">Provider family the text belongs to, or <see langword="null"/> when shared.</param>
	/// <param name="Origin">Type that produces the text.</param>
	/// <param name="IsTemplate">Whether <paramref name="Text"/> is a template with named or optional tokens.</param>
	public sealed record SqlTextItem(SqlTextKind Kind, string Text, string? Partition, string? Origin, bool IsTemplate);
}
