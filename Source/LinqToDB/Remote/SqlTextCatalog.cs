using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

using LinqToDB.Extensions;
using LinqToDB.Internal.Common;

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
		readonly Lock _sync = new();

		volatile HashSet<(SqlTextKind Kind, string Text)> _added   = new();
		volatile HashSet<(SqlTextKind Kind, string Text)> _removed = new();

		/// <summary>Allows <paramref name="text"/> for <paramref name="kind"/>.</summary>
		/// <param name="kind">Position the text may be used at.</param>
		/// <param name="text">SQL text.</param>
		public void Add(SqlTextKind kind, string text)
		{
			ArgumentNullException.ThrowIfNull(text);

			lock (_sync)
				_added = new(_added) { (kind, Canonicalize(text)) };
		}

		/// <summary>Removes <paramref name="text"/> for <paramref name="kind"/> from every source.</summary>
		/// <param name="kind">Position the text was allowed at.</param>
		/// <param name="text">SQL text.</param>
		public void Remove(SqlTextKind kind, string text)
		{
			ArgumentNullException.ThrowIfNull(text);

			lock (_sync)
				_removed = new(_removed) { (kind, Canonicalize(text)) };
		}

		internal RemoteRejectionReason? Check(SqlTextKind kind, string text, HashSet<(SqlTextKind Kind, string Text)> sources)
		{
			var key = (kind, Canonicalize(text));

			if (_removed.Contains(key))
				return RemoteRejectionReason.TextRemovedFromCatalog;

			if (_added.Contains(key) || sources.Contains(key))
				return null;

			var canonical = key.Item2;

			return _added.Concat(sources).Any(e => e.Kind != kind && string.Equals(e.Text, canonical, StringComparison.Ordinal))
				? RemoteRejectionReason.TextAllowedForOtherKind
				: RemoteRejectionReason.TextNotInCatalog;
		}

		static readonly ConcurrentDictionary<(Assembly Assembly, string? Family), HashSet<(SqlTextKind Kind, string Text)>> _assemblyCache = new();

		internal static HashSet<(SqlTextKind Kind, string Text)> GetAssemblySources(Assembly assembly, string? family)
		{
			return _assemblyCache.GetOrAdd((assembly, family), static key => Build(key.Assembly, key.Family));
		}

		static HashSet<(SqlTextKind Kind, string Text)> Build(Assembly assembly, string? family)
		{
			var result = new HashSet<(SqlTextKind Kind, string Text)>();

			foreach (var attribute in assembly.GetAttributes<SqlTextCatalogAttribute>())
			{
				var catalog = ActivatorExt.CreateInstance<ISqlTextCatalog>(attribute.CatalogType, true);

				foreach (var item in catalog.Items)
				{
					if (item.Partition != null && !string.Equals(item.Partition, family, StringComparison.Ordinal))
						continue;

					if (item.IsTemplate)
					{
						foreach (var text in ExpandTemplate(item.Text))
							result.Add((item.Kind, Canonicalize(text)));
					}
					else
					{
						result.Add((item.Kind, Canonicalize(item.Text)));
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

					while (end < text.Length && char.IsDigit(text[end]))
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

				while (end < text.Length && char.IsDigit(text[end]) && value < 100000)
					value = value * 10 + text[end++] - '0';

				if (end > i + 1 && end < text.Length && text[end] == '}')
					max = Math.Max(max, value);
			}

			return max + 1;
		}
	}
}
