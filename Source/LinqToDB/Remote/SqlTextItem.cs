namespace LinqToDB.Remote
{
	/// <summary>
	/// One text accepted from untrusted clients, or removed from what they may send.
	/// </summary>
	/// <param name="Kind">Position the text may be used at.</param>
	/// <param name="Text">SQL text.</param>
	/// <param name="Partition">Provider family the text belongs to, or <see langword="null"/> when shared.</param>
	/// <param name="Origin">Type that produces the text.</param>
	/// <param name="IsTemplate">Whether <paramref name="Text"/> is a template with named or optional tokens.</param>
	/// <param name="IsRemoval">
	/// Whether the item removes <paramref name="Text"/> instead of allowing it. A removal applies to every source of the
	/// service, the library's own catalog included.
	/// </param>
	public sealed record SqlTextItem(SqlTextKind Kind, string Text, string? Partition, string? Origin, bool IsTemplate, bool IsRemoval = false);
}
