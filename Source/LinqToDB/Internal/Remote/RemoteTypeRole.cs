namespace LinqToDB.Internal.Remote
{
	/// <summary>
	/// How a type read from a remote request is going to be used.
	/// </summary>
	public enum RemoteTypeRole
	{
		/// <summary>Type a value is converted to.</summary>
		Value,
		/// <summary>System type of a data type.</summary>
		DataType,
		/// <summary>Entity type of a table.</summary>
		Object,
		/// <summary>Query or table extension builder.</summary>
		Builder,
	}
}
