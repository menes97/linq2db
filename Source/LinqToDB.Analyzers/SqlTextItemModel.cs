namespace LinqToDB.Analyzers
{
	/// <summary>One catalog entry the generator emits; a removal withdraws the text from every source of the service.</summary>
	readonly record struct SqlTextItemModel(string Kind, string Text, string? Origin, bool IsTemplate, bool IsRemoval = false);
}
