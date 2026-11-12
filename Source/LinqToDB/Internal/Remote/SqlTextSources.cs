using System.Collections.Generic;

using LinqToDB.Remote;

namespace LinqToDB.Internal.Remote
{
	/// <summary>
	/// Library, provider and assembly catalog entries of one service for one provider, after the assemblies' own removals.
	/// </summary>
	internal sealed class SqlTextSources(
		IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> allowed,
		IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> removed,
		IReadOnlyCollection<(SqlTextKind Kind, string Text)>                 unmatchedRemovals)
	{
		/// <summary>Entries accepted from untrusted clients, with the source that first contributed each.</summary>
		public IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> Allowed { get; } = allowed;

		/// <summary>Removals declared by assembly catalogs, with the source that declares each. A removal wins over every source.</summary>
		public IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> Removed { get; } = removed;

		/// <summary>Assembly removals that match no entry of any source.</summary>
		public IReadOnlyCollection<(SqlTextKind Kind, string Text)> UnmatchedRemovals { get; } = unmatchedRemovals;
	}
}
