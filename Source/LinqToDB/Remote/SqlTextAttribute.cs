using System;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Marks a string parameter or property whose value is rendered as SQL text, so it can be catalogued.
	/// </summary>
	[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
	public sealed class SqlTextAttribute : Attribute
	{
		/// <summary>Creates the attribute.</summary>
		/// <param name="kind">Position the text is rendered at.</param>
		public SqlTextAttribute(SqlTextKind kind)
		{
			Kind = kind;
		}

		/// <summary>Gets the position the text is rendered at.</summary>
		public SqlTextKind Kind { get; }
	}
}
