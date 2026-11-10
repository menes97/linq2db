namespace LinqToDB.Internal.Remote
{
	/// <summary>Where an entry of the effective SQL text catalog comes from.</summary>
	/// <param name="Source">Name of the contributing assembly.</param>
	/// <param name="Origin">Type that produces the text, when known.</param>
	readonly record struct SqlTextOrigin(string Source, string? Origin);
}
