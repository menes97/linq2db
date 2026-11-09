using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Position in a remote request where SQL text is rendered. Text approved for one kind is not accepted for another.
	/// </summary>
	public enum SqlTextKind
	{
		/// <summary>SQL expression template.</summary>
		Expression,
		/// <summary>SQL fragment template.</summary>
		Fragment,
		/// <summary>Function name.</summary>
		Function,
		/// <summary>Table expression template.</summary>
		TableExpression,
		/// <summary>Binary operator.</summary>
		Operator,
		/// <summary>Query hint.</summary>
		QueryHint,
		/// <summary>Text argument of a query or table extension.</summary>
		ExtensionArgument,
		/// <summary>Hint of a merge statement.</summary>
		MergeHint,
		/// <summary>Text of a raw SQL table source.</summary>
		RawSqlTable,
		/// <summary>Header of a create table statement.</summary>
		StatementHeader,
		/// <summary>Footer of a create table statement.</summary>
		StatementFooter,
		/// <summary>Runtime override of a table, schema, database or server name.</summary>
		TableName,
	}

	/// <summary>
	/// Host-maintained SQL text accepted from untrusted clients of a <see cref="LinqService"/>.
	/// </summary>
	/// <remarks>
	/// An approved string is not tied to the place it was written: any client may send it at any position of its kind.
	/// Numbered placeholders are compared without their numbers. Removal wins over every source.
	/// </remarks>
	public sealed class SqlTextCatalog
	{
		readonly object _sync = new();

		volatile HashSet<(SqlTextKind Kind, string Text)> _added   = new();
		volatile HashSet<(SqlTextKind Kind, string Text)> _removed = new();

		/// <summary>Allows <paramref name="text"/> for <paramref name="kind"/>.</summary>
		/// <param name="kind">Position the text may be used at.</param>
		/// <param name="text">SQL text.</param>
		public void Add(SqlTextKind kind, string text)
		{
			if (text == null) throw new ArgumentNullException(nameof(text));

			lock (_sync)
				_added = new(_added) { (kind, Canonicalize(text)) };
		}

		/// <summary>Removes <paramref name="text"/> for <paramref name="kind"/> from every source.</summary>
		/// <param name="kind">Position the text was allowed at.</param>
		/// <param name="text">SQL text.</param>
		public void Remove(SqlTextKind kind, string text)
		{
			if (text == null) throw new ArgumentNullException(nameof(text));

			lock (_sync)
				_removed = new(_removed) { (kind, Canonicalize(text)) };
		}

		internal RemoteRejectionReason? Check(SqlTextKind kind, string text)
		{
			var key = (kind, Canonicalize(text));

			if (_removed.Contains(key))
				return RemoteRejectionReason.TextRemovedFromCatalog;

			if (_added.Contains(key))
				return null;

			var canonical = key.Item2;

			return _added.Any(e => e.Kind != kind && string.Equals(e.Text, canonical, StringComparison.Ordinal))
				? RemoteRejectionReason.TextAllowedForOtherKind
				: RemoteRejectionReason.TextNotInCatalog;
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
