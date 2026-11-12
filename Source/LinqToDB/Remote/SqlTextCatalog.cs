using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

using LinqToDB.Analyzers;
using LinqToDB.Extensions;
using LinqToDB.Internal.Common;
using LinqToDB.Internal.Remote;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Host-maintained SQL text accepted from untrusted clients of a <see cref="LinqService"/>.
	/// </summary>
	/// <remarks>
	/// An approved string is not tied to the place it was written: any client may send it at any position of its kind.
	/// Numbered placeholders are compared without their numbers. Removal wins over every source.
	/// </remarks>
	public sealed class SqlTextCatalog
	{
		/// <summary><see cref="SqlTextEntry.Source"/> of entries added through this catalog.</summary>
		public const string HostSource = "Host";

		readonly Lock _sync = new();

		// one snapshot, so a reader never sees the additions of an edit without its removals
		sealed class State(HashSet<(SqlTextKind Kind, string Text)> added, HashSet<(SqlTextKind Kind, string Text)> removed, int version)
		{
			public readonly HashSet<(SqlTextKind Kind, string Text)> Added   = added;
			public readonly HashSet<(SqlTextKind Kind, string Text)> Removed = removed;
			public readonly int                                      Version = version;
		}

		volatile State _state = new([], [], 0);

		/// <summary>Incremented on every edit, so cached catalog views and host-side reports can tell they are stale.</summary>
		internal int Version => _state.Version;

		/// <summary>Allows <paramref name="text"/> for <paramref name="kind"/>.</summary>
		/// <param name="kind">Position the text may be used at.</param>
		/// <param name="text">SQL text.</param>
		/// <exception cref="ArgumentException">
		/// <paramref name="text"/> has a format item inside a quoted literal or a comment, or leaves one of them open.
		/// A rendered argument would not be delimited there, so such text is never accepted.
		/// </exception>
		public void Add(SqlTextKind kind, string text)
		{
			ArgumentNullException.ThrowIfNull(text);

			if (GetRefusal(text) is { } refusal)
				throw new ArgumentException(refusal, nameof(text));

			lock (_sync)
			{
				var state = _state;
				_state = new([.. state.Added, (kind, Canonicalize(text))], state.Removed, state.Version + 1);
			}
		}

		/// <summary>Removes <paramref name="text"/> for <paramref name="kind"/> from every source.</summary>
		/// <param name="kind">Position the text was allowed at.</param>
		/// <param name="text">SQL text.</param>
		public void Remove(SqlTextKind kind, string text)
		{
			ArgumentNullException.ThrowIfNull(text);

			lock (_sync)
			{
				var state = _state;
				_state = new(state.Added, [.. state.Removed, (kind, Canonicalize(text))], state.Version + 1);
			}
		}

		/// <summary>
		/// Gets why <paramref name="text"/> can never be a catalog entry, whatever the provider, or <see langword="null"/>.
		/// Brackets, backticks and dollar quotes depend on the provider and are checked by <see cref="IsRefused"/>.
		/// </summary>
		internal static string? GetRefusal(string text)
		{
			// "{}" counts as a format item too: it is how exported text spells a placeholder
			return SqlTextQuoting.TryFindQuotedToken(text, SqlTextQuoting.Dialect.AlwaysOn, out var index)
				? string.Create(CultureInfo.InvariantCulture, $"The text has a format item inside a quoted literal or a comment, or leaves one open, at position {index}.")
				: null;
		}

		/// <summary>Whether <paramref name="text"/> must not be accepted for a provider with <paramref name="dialect"/>.</summary>
		internal static bool IsRefused(string text, SqlTextQuoting.Dialect dialect)
		{
			return SqlTextQuoting.TryFindQuotedToken(text, dialect, out _);
		}

		internal RemoteRejectionReason? Check(SqlTextKind kind, string text, IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources, SqlTextQuoting.Dialect dialect)
		{
			var state = _state;
			var key   = (kind, Canonicalize(text));

			if (state.Removed.Contains(key))
				return RemoteRejectionReason.TextRemovedFromCatalog;

			if (sources.ContainsKey(key) || state.Added.Contains(key) && !IsRefused(key.Item2, dialect))
				return null;

			var canonical = key.Item2;

			return state.Added.Concat(sources.Keys).Any(e => e.Kind != kind && string.Equals(e.Text, canonical, StringComparison.Ordinal))
				? RemoteRejectionReason.TextAllowedForOtherKind
				: RemoteRejectionReason.TextNotInCatalog;
		}

		/// <summary>Lists every entry of the effective catalog over <paramref name="sources"/>, and removals that match nothing.</summary>
		internal IReadOnlyList<SqlTextEntry> Dump(IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources, SqlTextQuoting.Dialect dialect)
		{
			var state   = _state;
			var entries = new List<SqlTextEntry>(sources.Count + state.Added.Count);

			foreach (var pair in sources)
				entries.Add(new(pair.Key.Kind, pair.Key.Text, pair.Value.Source, pair.Value.Origin, state.Removed.Contains(pair.Key) ? SqlTextEntryState.Removed : SqlTextEntryState.Allowed));

			foreach (var key in state.Added)
			{
				if (sources.ContainsKey(key))
					continue;

				var entryState =
					state.Removed.Contains(key)   ? SqlTextEntryState.Removed :
					IsRefused(key.Text, dialect) ? SqlTextEntryState.Refused :
					                               SqlTextEntryState.Allowed;

				entries.Add(new(key.Kind, key.Text, HostSource, null, entryState));
			}

			foreach (var (kind, text) in GetStaleRemovals(state, sources))
				entries.Add(new(kind, text, HostSource, null, SqlTextEntryState.StaleRemoval));

			entries.Sort(static (x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : string.CompareOrdinal(x.Text, y.Text));

			return entries;
		}

		/// <summary>Removals that match no entry of <paramref name="sources"/> and no host addition: usually a typo or a text the library no longer produces.</summary>
		internal IEnumerable<(SqlTextKind Kind, string Text)> GetStaleRemovals(IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources)
		{
			return GetStaleRemovals(_state, sources);
		}

		static IEnumerable<(SqlTextKind Kind, string Text)> GetStaleRemovals(State state, IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources)
		{
			return state.Removed.Where(key => !sources.ContainsKey(key) && !state.Added.Contains(key));
		}

		/// <summary>Writes the host's additions and removals in the format <see cref="Load"/> reads.</summary>
		/// <param name="writer">Target writer.</param>
		/// <remarks>Library, provider and mapping assembly entries are not written; use <see cref="Export(TextWriter, IEnumerable{SqlTextEntry})"/> with <see cref="LinqService.DumpSqlText"/> for the full effective set.</remarks>
		public void Export(TextWriter writer)
		{
			ArgumentNullException.ThrowIfNull(writer);

			var state = _state;

			Export(writer, state.Added.Where(key => !state.Removed.Contains(key)).Select(static key => new SqlTextEntry(key.Kind, key.Text, HostSource, null, SqlTextEntryState.Allowed))
				.Concat(state.Removed.Select(static key => new SqlTextEntry(key.Kind, key.Text, HostSource, null, SqlTextEntryState.Removed))));
		}

		static readonly string[] _kindNames = Enum.GetNames<SqlTextKind>();

		const string ExportHeader = "# linq2db SQL text catalog: '+' allows, '-' removes; kind and text are tab-separated; \\s is a trailing space, \\e an empty text";

		/// <summary>Writes <paramref name="entries"/> in the format <see cref="Load"/> reads: allowed entries as additions, the rest as removals.</summary>
		/// <param name="writer">Target writer.</param>
		/// <param name="entries">Entries to write, for example the result of <see cref="LinqService.DumpSqlText"/>.</param>
		public static void Export(TextWriter writer, IEnumerable<SqlTextEntry> entries)
		{
			ArgumentNullException.ThrowIfNull(writer);
			ArgumentNullException.ThrowIfNull(entries);

			writer.WriteLine(ExportHeader);

			foreach (var entry in entries)
				writer.WriteLine(SqlTextLineFormat.FormatEntry(entry.State == SqlTextEntryState.Allowed, entry.Kind.ToString(), entry.Text));
		}

		/// <summary>Applies additions and removals written by <see cref="Export(TextWriter)"/>.</summary>
		/// <param name="reader">Source reader.</param>
		/// <exception cref="FormatException">
		/// A line is not a valid entry, or adds a text <see cref="Add"/> refuses. Nothing is applied in that case.
		/// </exception>
		public void Load(TextReader reader)
		{
			ArgumentNullException.ThrowIfNull(reader);

			var adds    = new List<(SqlTextKind Kind, string Text)>();
			var removes = new List<(SqlTextKind Kind, string Text)>();
			var number  = 0;

			while (reader.ReadLine() is { } line)
			{
				number++;

				if (!SqlTextLineFormat.TryParseEntry(line, out var add, out var kindName, out var text, out var error))
				{
					if (error == null)
						continue;

					throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number}: {error}."));
				}

				// a kind is matched by declared name only: TryParse alone also accepts numbers outside the enum
				if (!_kindNames.Contains(kindName, StringComparer.Ordinal) || !Enum.TryParse<SqlTextKind>(kindName, false, out var kind))
					throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number}: unknown kind '{kindName}'."));

				if (add && GetRefusal(text) is { } refusal)
					throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number}: {refusal}"));

				(add ? adds : removes).Add((kind, Canonicalize(text)));
			}

			lock (_sync)
			{
				var state = _state;
				_state = new([.. state.Added, .. adds], [.. state.Removed, .. removes], state.Version + 1);
			}
		}

		static readonly ConcurrentDictionary<(Assembly Assembly, string? Family), Dictionary<(SqlTextKind Kind, string Text), string?>> _assemblyCache = new();

		/// <summary>Gets the entries an assembly's catalogs contribute, with the type that produces each text.</summary>
		internal static IReadOnlyDictionary<(SqlTextKind Kind, string Text), string?> GetAssemblySources(Assembly assembly, string? family)
		{
			return _assemblyCache.GetOrAdd((assembly, family), static key => Build(key.Assembly, key.Family));
		}

		static Dictionary<(SqlTextKind Kind, string Text), string?> Build(Assembly assembly, string? family)
		{
			var result = new Dictionary<(SqlTextKind Kind, string Text), string?>();

			foreach (var attribute in assembly.GetAttributes<SqlTextCatalogAttribute>())
			{
				var catalog = ActivatorExt.CreateInstance<ISqlTextCatalog>(attribute.CatalogType, true);

				foreach (var item in catalog.Items)
				{
					if (item.Partition != null && !string.Equals(item.Partition, family, StringComparison.Ordinal))
						continue;

					var texts = item.IsTemplate ? ExpandTemplate(item.Text) : [item.Text];

					foreach (var text in texts)
					{
						var key = (item.Kind, Canonicalize(text));

						if (!result.ContainsKey(key))
							result.Add(key, item.Origin);
					}
				}
			}

			return result;
		}

		const int MaxOptionalTokens = 6;

		internal static IReadOnlyList<string> ExpandTemplate(string template)
		{
			var tokens   = new List<string>();
			var hasList  = false;

			Sql.ExpressionAttribute.ResolveExpressionValues(
				(object?)null,
				template,
				(_, name, delimiter) =>
				{
					hasList |= delimiter != null;

					if (!tokens.Contains(name, StringComparer.Ordinal))
						tokens.Add(name);

					return "{0}";
				},
				out _);

			if (hasList)
				return [];

			var optional = tokens.Where(t => template.Contains("{" + t + "?", StringComparison.Ordinal)).ToList();

			if (optional.Count > MaxOptionalTokens)
				return [];

			var results = new List<string>(1 << optional.Count);

			for (var mask = 0; mask < 1 << optional.Count; mask++)
			{
				var absent = new HashSet<string>(optional.Where((_, i) => (mask & (1 << i)) != 0), StringComparer.Ordinal);

				var text = Sql.ExpressionAttribute.ResolveExpressionValues(
					absent,
					template,
					static (a, name, _) => a.Contains(name) ? null : "{0}",
					out _);

				results.Add(text);
			}

			return results;
		}

		internal static string Canonicalize(string text)
		{
			var sb = new StringBuilder(text.Length);

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '{' && i + 1 < text.Length)
				{
					if (text[i + 1] == '{')
					{
						sb.Append("{{");
						i++;
						continue;
					}

					var end = i + 1;

					while (end < text.Length && IsAsciiDigit(text[end]))
						end++;

					if (end > i + 1 && end < text.Length && text[end] == '}')
					{
						sb.Append("{}");
						i = end;
						continue;
					}
				}

				sb.Append(text[i]);
			}

			return sb.ToString();
		}

		internal static int GetPlaceholderCount(string text)
		{
			var max = -1;

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '{')
					continue;

				if (i + 1 < text.Length && text[i + 1] == '{')
				{
					i++;
					continue;
				}

				var end   = i + 1;
				var value = 0;

				while (end < text.Length && IsAsciiDigit(text[end]))
				{
					// an index this large cannot match any argument list; report it as out of range rather than stop reading it
					if (value > MaxPlaceholderIndex)
						return int.MaxValue;

					value = value * 10 + text[end++] - '0';
				}

				if (end > i + 1 && end < text.Length && text[end] == '}')
				{
					if (value > MaxPlaceholderIndex)
						return int.MaxValue;

					max = Math.Max(max, value);
				}
			}

			return max + 1;
		}

		/// <summary>
		/// Whether <paramref name="text"/> may refer to the format item <paramref name="index"/>. Errs towards <see langword="true"/>:
		/// any unescaped brace followed by those digits counts, with spaces before them and anything after them (<c>{0 }</c>).
		/// </summary>
		internal static bool UsesPlaceholder(string text, int index)
		{
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '{')
					continue;

				if (i + 1 < text.Length && text[i + 1] == '{')
				{
					i++;
					continue;
				}

				var start = i + 1;

				while (start < text.Length && text[start] == ' ')
					start++;

				var end   = start;
				var value = 0;

				while (end < text.Length && IsAsciiDigit(text[end]) && value <= MaxPlaceholderIndex)
					value = value * 10 + text[end++] - '0';

				if (end > start && value == index)
					return true;
			}

			return false;
		}

		const int MaxPlaceholderIndex = 100000;

		// char.IsDigit accepts every Unicode decimal digit, which String.Format does not read as an index
		static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
	}
}
