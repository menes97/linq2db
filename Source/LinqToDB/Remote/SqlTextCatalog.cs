using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

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

		volatile HashSet<(SqlTextKind Kind, string Text)> _added   = new();
		volatile HashSet<(SqlTextKind Kind, string Text)> _removed = new();

		/// <summary>Incremented on every edit, so a host-side report of the catalog can tell it is stale.</summary>
		internal int Version { get; private set; }

		/// <summary>Allows <paramref name="text"/> for <paramref name="kind"/>.</summary>
		/// <param name="kind">Position the text may be used at.</param>
		/// <param name="text">SQL text.</param>
		public void Add(SqlTextKind kind, string text)
		{
			ArgumentNullException.ThrowIfNull(text);

			lock (_sync)
			{
				_added = new(_added) { (kind, Canonicalize(text)) };
				Version++;
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
				_removed = new(_removed) { (kind, Canonicalize(text)) };
				Version++;
			}
		}

		internal RemoteRejectionReason? Check(SqlTextKind kind, string text, IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources)
		{
			var key = (kind, Canonicalize(text));

			if (_removed.Contains(key))
				return RemoteRejectionReason.TextRemovedFromCatalog;

			if (_added.Contains(key) || sources.ContainsKey(key))
				return null;

			var canonical = key.Item2;

			return _added.Concat(sources.Keys).Any(e => e.Kind != kind && string.Equals(e.Text, canonical, StringComparison.Ordinal))
				? RemoteRejectionReason.TextAllowedForOtherKind
				: RemoteRejectionReason.TextNotInCatalog;
		}

		/// <summary>Lists every entry of the effective catalog over <paramref name="sources"/>, and removals that match nothing.</summary>
		internal IReadOnlyList<SqlTextEntry> Dump(IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources)
		{
			var added   = _added;
			var removed = _removed;
			var entries = new List<SqlTextEntry>(sources.Count + added.Count);

			foreach (var pair in sources)
				entries.Add(new(pair.Key.Kind, pair.Key.Text, pair.Value.Source, pair.Value.Origin, removed.Contains(pair.Key) ? SqlTextEntryState.Removed : SqlTextEntryState.Allowed));

			foreach (var key in added)
				if (!sources.ContainsKey(key))
					entries.Add(new(key.Kind, key.Text, HostSource, null, removed.Contains(key) ? SqlTextEntryState.Removed : SqlTextEntryState.Allowed));

			foreach (var (kind, text) in GetStaleRemovals(sources))
				entries.Add(new(kind, text, HostSource, null, SqlTextEntryState.StaleRemoval));

			entries.Sort(static (x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : string.CompareOrdinal(x.Text, y.Text));

			return entries;
		}

		/// <summary>Removals that match no entry of <paramref name="sources"/> and no host addition: usually a typo or a text the library no longer produces.</summary>
		internal IEnumerable<(SqlTextKind Kind, string Text)> GetStaleRemovals(IReadOnlyDictionary<(SqlTextKind Kind, string Text), SqlTextOrigin> sources)
		{
			var added = _added;

			return _removed.Where(key => !sources.ContainsKey(key) && !added.Contains(key));
		}

		/// <summary>Writes the host's additions and removals in the format <see cref="Load"/> reads.</summary>
		/// <param name="writer">Target writer.</param>
		/// <remarks>Library, provider and mapping assembly entries are not written; use <see cref="Export(TextWriter, IEnumerable{SqlTextEntry})"/> with <see cref="LinqService.DumpSqlText"/> for the full effective set.</remarks>
		public void Export(TextWriter writer)
		{
			ArgumentNullException.ThrowIfNull(writer);

			var removed = _removed;

			Export(writer, _added.Where(key => !removed.Contains(key)).Select(static key => new SqlTextEntry(key.Kind, key.Text, HostSource, null, SqlTextEntryState.Allowed))
				.Concat(removed.Select(static key => new SqlTextEntry(key.Kind, key.Text, HostSource, null, SqlTextEntryState.Removed))));
		}

		static readonly string[] _kindNames = Enum.GetNames<SqlTextKind>();

		const string ExportHeader ="# linq2db SQL text catalog: '+' allows, '-' removes; kind and text are tab-separated";

		/// <summary>Writes <paramref name="entries"/> in the format <see cref="Load"/> reads: allowed entries as additions, the rest as removals.</summary>
		/// <param name="writer">Target writer.</param>
		/// <param name="entries">Entries to write, for example the result of <see cref="LinqService.DumpSqlText"/>.</param>
		public static void Export(TextWriter writer, IEnumerable<SqlTextEntry> entries)
		{
			ArgumentNullException.ThrowIfNull(writer);
			ArgumentNullException.ThrowIfNull(entries);

			writer.WriteLine(ExportHeader);

			foreach (var entry in entries)
			{
				writer.Write(entry.State == SqlTextEntryState.Allowed ? '+' : '-');
				writer.Write('\t');
				writer.Write(entry.Kind.ToString());
				writer.Write('\t');
				writer.WriteLine(Escape(entry.Text));
			}
		}

		/// <summary>Applies additions and removals written by <see cref="Export(TextWriter)"/>.</summary>
		/// <param name="reader">Source reader.</param>
		/// <exception cref="FormatException">A line is not a valid entry. Nothing is applied in that case.</exception>
		public void Load(TextReader reader)
		{
			ArgumentNullException.ThrowIfNull(reader);

			var adds    = new List<(SqlTextKind Kind, string Text)>();
			var removes = new List<(SqlTextKind Kind, string Text)>();
			var number  = 0;

			while (reader.ReadLine() is { } line)
			{
				number++;

				if (line.Length == 0 || line[0] == '#')
					continue;

				var parts = line.Split('\t');

				// a kind is matched by declared name only: TryParse alone also accepts numbers outside the enum
				if (parts.Length != 3
					|| parts[0] is not ("+" or "-")
					|| !_kindNames.Contains(parts[1], StringComparer.Ordinal)
					|| !Enum.TryParse<SqlTextKind>(parts[1], false, out var kind))
				{
					throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number} is not '+' or '-', a kind and a text separated by tabs."));
				}

				(string.Equals(parts[0], "+", StringComparison.Ordinal) ? adds : removes).Add((kind, Unescape(parts[2], number)));
			}

			lock (_sync)
			{
				_added   = [.. _added,   .. adds   .Select(static e => (e.Kind, Canonicalize(e.Text)))];
				_removed = [.. _removed, .. removes.Select(static e => (e.Kind, Canonicalize(e.Text)))];
				Version++;
			}
		}

		static string Escape(string text)
		{
			return text
				.Replace("\\", "\\\\", StringComparison.Ordinal)
				.Replace("\t", "\\t",  StringComparison.Ordinal)
				.Replace("\r", "\\r",  StringComparison.Ordinal)
				.Replace("\n", "\\n",  StringComparison.Ordinal);
		}

		static string Unescape(string text, int number)
		{
			var sb = new StringBuilder(text.Length);

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '\\')
				{
					sb.Append(text[i]);
					continue;
				}

				if (++i == text.Length)
					throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number} ends with an escape character."));

				sb.Append(text[i] switch
				{
					'\\' => '\\',
					't'  => '\t',
					'r'  => '\r',
					'n'  => '\n',
					_    => throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"SQL text catalog line {number} has an unknown escape '\\{text[i]}'.")),
				});
			}

			return sb.ToString();
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
