using System;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// Finds format items that sit inside a quoted literal, a quoted identifier or a comment of an SQL text.
	/// Such text is never accepted as a <c>SqlTextCatalog</c> entry, because a rendered argument there is not
	/// delimited the way the template's author expects.
	/// </summary>
	/// <remarks>
	/// Compiled into <c>LinqToDB</c>, <c>CodeGenerators</c> and <c>LinqToDB.Analyzers</c>: BCL only, no polyfilled API.
	/// The scan errs towards reporting. A quote is read both with doubled-delimiter escapes and with backslash escapes,
	/// and the text is reported when either reading finds a format item inside a context or leaves a context open.
	/// </remarks>
	internal static class SqlTextQuoting
	{
		/// <summary>Quoting contexts recognized by <see cref="TryFindQuotedToken"/>.</summary>
		[Flags]
		internal enum Dialect
		{
			None        = 0,
			/// <summary><c>'...'</c>.</summary>
			Apostrophe  = 1,
			/// <summary><c>"..."</c>.</summary>
			DoubleQuote = 2,
			/// <summary><c>-- ...</c> to the end of the line and <c>/* ... */</c>.</summary>
			Comments    = 4,
			/// <summary><c>[...]</c>.</summary>
			Brackets    = 8,
			/// <summary><c>`...`</c>.</summary>
			Backtick    = 16,
			/// <summary><c>$tag$...$tag$</c>.</summary>
			DollarQuote = 32,
			/// <summary><c>/* ... */</c> nests: each <c>/*</c> inside a comment needs its own <c>*/</c>.</summary>
			NestedComments = 64,
			/// <summary><c># ...</c> to the end of the line.</summary>
			HashComments = 128,
			/// <summary>Oracle <c>q'[...]'</c>, <c>q'{...}'</c>, <c>q'(...)'</c>, <c>q'&lt;...&gt;'</c> and <c>q'c...c'</c>.</summary>
			QQuote = 256,

			/// <summary>Contexts that quote in every supported database. Used where the database is not known yet.</summary>
			AlwaysOn    = Apostrophe | DoubleQuote | Comments,
			/// <summary>Every context. Used for text shared by all databases and for databases outside the library.</summary>
			All         = AlwaysOn | Brackets | Backtick | DollarQuote | NestedComments | HashComments | QQuote,
		}

		/// <summary>Gets the contexts of a provider family, the <c>Internal.DataProvider.&lt;family&gt;</c> namespace segment.</summary>
		/// <param name="family">Provider family, or <see langword="null"/> for text shared by all providers.</param>
		internal static Dialect GetDialect(string? family)
		{
			return family switch
			{
				// nesting is assumed wherever it is not known to be absent: reading a comment as nested only widens it
				"SqlServer" or "SqlCe" or "Sybase"            => Dialect.AlwaysOn | Dialect.NestedComments | Dialect.Brackets,
				"Access"                                      => Dialect.AlwaysOn | Dialect.NestedComments | Dialect.Brackets | Dialect.Backtick,
				"SQLite"                                      => Dialect.AlwaysOn | Dialect.Brackets | Dialect.Backtick,
				"MySql"                                       => Dialect.AlwaysOn | Dialect.HashComments | Dialect.Backtick,
				"Ydb"                                         => Dialect.AlwaysOn | Dialect.NestedComments | Dialect.Backtick,
				"ClickHouse"                                  => Dialect.AlwaysOn | Dialect.NestedComments | Dialect.HashComments | Dialect.Backtick | Dialect.DollarQuote,
				"PostgreSQL" or "DuckDB"                      => Dialect.AlwaysOn | Dialect.NestedComments | Dialect.DollarQuote,
				"Oracle"                                      => Dialect.AlwaysOn | Dialect.QQuote,
				"DB2" or "Firebird" or "Informix" or "SapHana" => Dialect.AlwaysOn | Dialect.NestedComments,
				_                                             => Dialect.All,
			};
		}

		/// <summary>
		/// Reports whether <paramref name="text"/> has a format item (<c>{0}</c>, <c>{name}</c>, <c>{name?}</c>) inside a
		/// context of <paramref name="dialect"/>, or leaves such a context open at its end.
		/// </summary>
		/// <param name="text">SQL text or template.</param>
		/// <param name="dialect">Contexts to recognize.</param>
		/// <param name="index">Position of the format item, or of the context left open.</param>
		/// <returns><see langword="true"/> when the text must not be catalogued.</returns>
		internal static bool TryFindQuotedToken(string text, Dialect dialect, out int index)
		{
			return Scan(text, dialect, false, out index) || Scan(text, dialect, true, out index);
		}

		static bool Scan(string text, Dialect dialect, bool backslashEscapes, out int index)
		{
			var i = 0;

			while (i < text.Length)
			{
				var c     = text[i];
				var start = i;

				if (c == '\'' && dialect.HasFlag(Dialect.Apostrophe)
					|| c == '"' && dialect.HasFlag(Dialect.DoubleQuote)
					|| c == '`' && dialect.HasFlag(Dialect.Backtick))
				{
					if (ScanDelimited(text, ref i, c, backslashEscapes, out index))
						return true;
				}
				else if (c == '[' && dialect.HasFlag(Dialect.Brackets))
				{
					if (ScanDelimited(text, ref i, ']', false, out index))
						return true;
				}
				else if (c is 'q' or 'Q' or 'n' or 'N' && dialect.HasFlag(Dialect.QQuote) && TryReadQQuote(text, i, out var bodyStart, out var closer))
				{
					var close = text.IndexOf(closer + "'", bodyStart, StringComparison.Ordinal);
					var end   = close < 0 ? text.Length : close;

					for (var j = bodyStart; j < end; j = Next(text, j))
					{
						if (IsTokenStart(text, j))
						{
							index = j;
							return true;
						}
					}

					if (close < 0)
					{
						index = start;
						return true;
					}

					i = close + 2;
				}
				else if ((c == '-' && dialect.HasFlag(Dialect.Comments) && i + 1 < text.Length && text[i + 1] == '-')
					|| (c == '#' && dialect.HasFlag(Dialect.HashComments)))
				{
					i += c == '#' ? 1 : 2;

					while (i < text.Length && text[i] != '\n' && text[i] != '\r')
					{
						if (IsTokenStart(text, i))
						{
							index = i;
							return true;
						}

						i = Next(text, i);
					}

					// a comment that runs to the end of the text also covers whatever is rendered after it
					if (i >= text.Length)
					{
						index = start;
						return true;
					}
				}
				else if (c == '/' && dialect.HasFlag(Dialect.Comments) && i + 1 < text.Length && text[i + 1] == '*')
				{
					i += 2;

					var depth = 1;

					while (true)
					{
						if (i + 1 >= text.Length)
						{
							index = start;
							return true;
						}

						if (text[i] == '*' && text[i + 1] == '/')
						{
							i += 2;

							if (--depth == 0)
								break;

							continue;
						}

						if (text[i] == '/' && text[i + 1] == '*' && dialect.HasFlag(Dialect.NestedComments))
						{
							i += 2;
							depth++;
							continue;
						}

						if (IsTokenStart(text, i))
						{
							index = i;
							return true;
						}

						i = Next(text, i);
					}
				}
				else if (c == '$' && dialect.HasFlag(Dialect.DollarQuote) && TryReadDollarTag(text, i, out var tagLength))
				{
					var close = text.IndexOf(text.Substring(i, tagLength), i + tagLength, StringComparison.Ordinal);
					var end   = close < 0 ? text.Length : close;

					for (var j = i + tagLength; j < end; j = Next(text, j))
					{
						if (IsTokenStart(text, j))
						{
							index = j;
							return true;
						}
					}

					// an opener without a closer is ordinary text, as in the $Length$ pseudo functions
					i = close < 0 ? i + 1 : close + tagLength;
				}
				else
				{
					i = Next(text, i);
				}
			}

			index = -1;
			return false;
		}

		// reads from the opening delimiter at i to just past its closer; a doubled closer is an escaped one
		static bool ScanDelimited(string text, ref int i, char closer, bool backslashEscapes, out int index)
		{
			var start = i;

			i++;

			while (true)
			{
				if (i >= text.Length)
				{
					index = start;
					return true;
				}

				var c = text[i];

				if (backslashEscapes && c == '\\')
				{
					i += 2;
					continue;
				}

				if (c == closer)
				{
					if (i + 1 < text.Length && text[i + 1] == closer)
					{
						i += 2;
						continue;
					}

					i++;
					index = -1;
					return false;
				}

				if (IsTokenStart(text, i))
				{
					index = i;
					return true;
				}

				i = Next(text, i);
			}
		}

		// '{' starts a format item unless it is the escaped "{{"
		static bool IsTokenStart(string text, int i)
		{
			return text[i] == '{' && !(i + 1 < text.Length && text[i + 1] == '{');
		}

		static int Next(string text, int i)
		{
			return text[i] == '{' && i + 1 < text.Length && text[i + 1] == '{' ? i + 2 : i + 1;
		}

		// $$ or $tag$, where tag is an identifier and the opener does not continue an identifier or a number
		static bool TryReadDollarTag(string text, int i, out int length)
		{
			length = 0;

			if (i > 0 && IsIdentifierChar(text[i - 1]))
				return false;

			var j = i + 1;

			if (j < text.Length && IsIdentifierStart(text[j]))
			{
				j++;

				while (j < text.Length && IsIdentifierChar(text[j]))
					j++;
			}

			if (j >= text.Length || text[j] != '$')
				return false;

			length = j - i + 1;
			return true;
		}

		// q'<d>...<d>' or nq'<d>...<d>', not continuing an identifier; a bracket delimiter closes with its pair
		static bool TryReadQQuote(string text, int i, out int bodyStart, out char closer)
		{
			bodyStart = 0;
			closer    = '\0';

			if (i > 0 && IsIdentifierChar(text[i - 1]))
				return false;

			var q = text[i] is 'n' or 'N' ? i + 1 : i;

			if (q + 2 >= text.Length || text[q] is not ('q' or 'Q') || text[q + 1] != '\'')
				return false;

			var open = text[q + 2];

			if (char.IsWhiteSpace(open))
				return false;

			closer = open switch
			{
				'[' => ']',
				'{' => '}',
				'(' => ')',
				'<' => '>',
				_   => open,
			};

			bodyStart = q + 3;
			return true;
		}

		static bool IsIdentifierStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
		static bool IsIdentifierChar (char c) => IsIdentifierStart(c) || c is >= '0' and <= '9' or '$';
	}
}
