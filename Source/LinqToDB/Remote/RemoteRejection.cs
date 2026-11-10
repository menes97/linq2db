namespace LinqToDB.Remote
{
	/// <summary>
	/// One violation found in a rejected request.
	/// </summary>
	public sealed class RemoteRejection
	{
		/// <summary>Creates a rejection.</summary>
		/// <param name="reason">Why the request was rejected.</param>
		/// <param name="kind">Text position, when the violation concerns SQL text.</param>
		/// <param name="text">Rejected text or type name, as sent by the client.</param>
		/// <param name="nodeType">Query element or role where the violation was found.</param>
		/// <param name="remedy">What the host can do when this is a false rejection.</param>
		public RemoteRejection(RemoteRejectionReason reason, SqlTextKind? kind, string text, string? nodeType, string remedy)
		{
			Reason   = reason;
			Kind     = kind;
			Text     = text;
			NodeType = nodeType;
			Remedy   = remedy;
		}

		/// <summary>Gets why the request was rejected.</summary>
		public RemoteRejectionReason Reason { get; }
		/// <summary>Gets the text position, when the violation concerns SQL text.</summary>
		public SqlTextKind? Kind { get; }
		/// <summary>Gets the rejected text or type name, as sent by the client.</summary>
		public string Text { get; }
		/// <summary>Gets the query element or role where the violation was found.</summary>
		public string? NodeType { get; }
		/// <summary>Gets what the host can do when this is a false rejection.</summary>
		public string Remedy { get; }
	}
}
