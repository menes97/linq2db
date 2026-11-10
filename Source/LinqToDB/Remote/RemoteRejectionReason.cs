namespace LinqToDB.Remote
{
	/// <summary>
	/// Reason why <see cref="LinqService"/> rejected part of a request from an untrusted client.
	/// </summary>
	public enum RemoteRejectionReason
	{
		/// <summary>The text is not in the catalog for its kind.</summary>
		TextNotInCatalog,
		/// <summary>The text was removed from the catalog.</summary>
		TextRemovedFromCatalog,
		/// <summary>The text is in the catalog, but for a different kind.</summary>
		TextAllowedForOtherKind,
		/// <summary>A placeholder refers to an argument that does not exist.</summary>
		PlaceholderOutOfRange,
		/// <summary>An identifier neither resolves through the server mapping nor has an allowed shape.</summary>
		IdentifierNotMapped,
		/// <summary>The type is not allowed in this position.</summary>
		TypeNotAllowed,
		/// <summary>The extension builder type is not known to the server.</summary>
		BuilderNotAllowed,
		/// <summary>The connection configuration is not allowed.</summary>
		ConfigurationNotAllowed,
		/// <summary>The query element is not allowed.</summary>
		NodeNotAllowed,
		/// <summary>An enumeration value is not defined.</summary>
		InvalidEnumValue,
	}
}
