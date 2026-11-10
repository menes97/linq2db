using System.Collections.Generic;

namespace LinqToDB.Remote
{
	/// <summary>
	/// SQL text an assembly contributes to the catalog.
	/// </summary>
	public interface ISqlTextCatalog
	{
		/// <summary>Gets the text items.</summary>
		IReadOnlyList<SqlTextItem> Items { get; }
	}
}
