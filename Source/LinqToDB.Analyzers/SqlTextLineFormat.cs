using System;
using System.Globalization;
using System.Text;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// Line format of SQL text catalog files: the committed <c>SqlTextCatalog.baseline.txt</c> and the files
	/// <c>SqlTextCatalog.Export</c> writes and <c>SqlTextCatalog.Load</c> reads.
	/// </summary>
	/// <remarks>
	/// Compiled into <c>LinqToDB</c>, <c>CodeGenerators</c> and <c>LinqToDB.Analyzers</c>: BCL only, no polyfilled API.
	/// <para>
	/// A text is written with <c>\\</c>, <c>\t</c>, <c>\r</c>, <c>\n</c>, <c>\uXXXX</c> for other control and non-ASCII
	/// whitespace characters, <c>\s</c> for each trailing space and <c>\e</c> for an empty text, so a line never ends in
	/// whitespace an editor could strip and never holds a character that makes tools treat the file as binary.
	/// </para>
	/// </remarks>
	internal static class SqlTextLineFormat
	{
		/// <summary>Escapes <paramref name="text"/> for one tab-separated field.</summary>
		internal static string Escape(string text)
		{
			if (text.Length == 0)
				return "\\e";

			var trailing = 0;

			while (trailing < text.Length && text[text.Length - 1 - trailing] == ' ')
				trailing++;

			var sb = new StringBuilder(text.Length + 8);

			for (var i = 0; i < text.Length - trailing; i++)
			{
				var c = text[i];

				switch (c)
				{
					case '\\': sb.Append("\\\\"); break;
					case '\t': sb.Append("\\t");  break;
					case '\r': sb.Append("\\r");  break;
					case '\n': sb.Append("\\n");  break;
					default:
						if (c < ' ' || c == (char)0x7F || c > (char)0x7F && (char.IsWhiteSpace(c) || c == (char)0xFEFF))
							sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
						else
							sb.Append(c);
						break;
				}
			}

			for (var i = 0; i < trailing; i++)
				sb.Append("\\s");

			return sb.ToString();
		}

		/// <summary>Reverses <see cref="Escape"/>.</summary>
		/// <param name="field">Escaped field.</param>
		/// <param name="text">Text, when the field is valid.</param>
		/// <param name="error">Reason the field is invalid.</param>
		internal static bool TryUnescape(string field, out string text, out string? error)
		{
			text  = "";
			error = null;

			if (string.Equals(field, "\\e", StringComparison.Ordinal))
				return true;

			if (field.Length == 0)
			{
				error = "the text is empty; an empty text is written as \\e";
				return false;
			}

			if (field[field.Length - 1] is ' ' or '\t')
			{
				error = "the text ends in whitespace; trailing spaces are written as \\s";
				return false;
			}

			var sb = new StringBuilder(field.Length);

			for (var i = 0; i < field.Length; i++)
			{
				if (field[i] != '\\')
				{
					sb.Append(field[i]);
					continue;
				}

				if (++i == field.Length)
				{
					error = "the text ends with an escape character";
					return false;
				}

				switch (field[i])
				{
					case '\\': sb.Append('\\'); break;
					case 't' : sb.Append('\t'); break;
					case 'r' : sb.Append('\r'); break;
					case 'n' : sb.Append('\n'); break;
					case 's' : sb.Append(' ');  break;
					case 'u' :
						var code = 0;

						for (var k = 1; k <= 4; k++)
						{
							var digit = i + k < field.Length ? HexValue(field[i + k]) : -1;

							if (digit < 0)
							{
								error = "\\u is not followed by four hexadecimal digits";
								return false;
							}

							code = code * 16 + digit;
						}

						sb.Append((char)code);
						i += 4;
						break;
					default:
						error = "unknown escape '\\" + field[i] + "'";
						return false;
				}
			}

			text = sb.ToString();
			return true;
		}

		static int HexValue(char c) => c switch
		{
			>= '0' and <= '9' => c - '0',
			>= 'A' and <= 'F' => c - 'A' + 10,
			>= 'a' and <= 'f' => c - 'a' + 10,
			_                 => -1,
		};

		/// <summary>Formats an entry line of an export file: <c>+</c> or <c>-</c>, the kind name and the text.</summary>
		internal static string FormatEntry(bool add, string kind, string text)
		{
			return (add ? "+" : "-") + "\t" + kind + "\t" + Escape(text);
		}

		/// <summary>
		/// Parses an entry line of an export file. Returns <see langword="false"/> with a <see langword="null"/>
		/// <paramref name="error"/> for a blank or comment line.
		/// </summary>
		internal static bool TryParseEntry(string line, out bool add, out string kind, out string text, out string? error)
		{
			add   = false;
			kind  = "";
			text  = "";
			error = null;

			line = line.TrimEnd('\r');

			if (line.Length == 0 || line[0] == '#')
				return false;

			var parts = line.Split('\t');

			var isAdd    = string.Equals(parts[0], "+", StringComparison.Ordinal);
			var isRemove = string.Equals(parts[0], "-", StringComparison.Ordinal);

			if (parts.Length != 3 || !isAdd && !isRemove || parts[1].Length == 0)
			{
				error = "the line is not '+' or '-', a kind and a text separated by tabs";
				return false;
			}

			if (!TryUnescape(parts[2], out text, out error))
				return false;

			add  = isAdd;
			kind = parts[1];
			return true;
		}

		/// <summary>Formats a baseline line: the kind name, the partition (<c>-</c> for shared text) and the text.</summary>
		internal static string FormatBaseline(string kind, string? partition, string text)
		{
			return kind + "\t" + (partition ?? "-") + "\t" + Escape(text);
		}
	}
}
