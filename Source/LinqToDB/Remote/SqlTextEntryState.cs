namespace LinqToDB.Remote
{
	/// <summary>
	/// State of a <see cref="SqlTextEntry"/>.
	/// </summary>
	public enum SqlTextEntryState
	{
		/// <summary>The text is accepted.</summary>
		Allowed,
		/// <summary>The text is removed by the host and rejected.</summary>
		Removed,
		/// <summary>A host removal that matches no entry: usually a typo, or a text the library no longer produces.</summary>
		StaleRemoval,
		/// <summary>
		/// A host addition with a format item inside a quoted identifier or a dollar-quoted string of the active provider,
		/// where a rendered argument is not delimited. It is not accepted.
		/// </summary>
		Refused,
	}
}
