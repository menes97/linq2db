using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// Reads <c>LinqToDBSqlText</c> allow and deny files: <c>+</c> or <c>-</c>, a <c>SqlTextKind</c> name and the escaped text,
	/// separated by tabs, as <c>SqlTextCatalog.Export</c> writes them.
	/// </summary>
	static class SqlTextFiles
	{
		public static void Read(AdditionalText file, SqlTextUserCatalogGenerator.Settings settings, SqlTextCollector collector, CancellationToken cancellationToken)
		{
			if (!TryGetOrigin(file.Path, settings, out var origin))
			{
				collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.FileLocationRule, Location.None, file.Path));
				return;
			}

			if (file.GetText(cancellationToken) is not { } text)
				return;

			foreach (var line in text.Lines)
			{
				var content = line.ToString();

				if (content.Length == 0 || content[0] == '#')
					continue;

				var location = Location.Create(file.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span));
				var parts    = content.Split('\t');
				var number   = (line.LineNumber + 1).ToString(CultureInfo.InvariantCulture);

				if (parts.Length != 3 || parts[0] is not ("+" or "-") || !collector.Kinds.Contains(parts[1]))
				{
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.MalformedLineRule, location,
						"Line " + number + " of '" + origin + "' is not '+' or '-', a SqlTextKind name and a text separated by tabs"));
					continue;
				}

				if (!TryUnescape(parts[2], out var value, out var error))
				{
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.MalformedLineRule, location, "Line " + number + " of '" + origin + "': " + error));
					continue;
				}

				if (string.Equals(parts[0], "-", StringComparison.Ordinal))
				{
					// GHSA-B-TEMP: removals need the SqlTextItem removal flag; until it lands the line is reported, never dropped silently.
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.UnsupportedRule, location,
						"Line " + number + " of '" + origin + "' removes an entry, which the referenced linq2db cannot express in a generated catalog"));
					continue;
				}

				collector.AddFileEntry(parts[1], value, origin);
			}
		}

		// GHSA-B-TEMP: replaced by the shared SqlTextLineFormat.cs once it lands.
		static bool TryUnescape(string text, out string value, out string? error)
		{
			var sb = new StringBuilder(text.Length);

			value = "";
			error = null;

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '\\')
				{
					sb.Append(text[i]);
					continue;
				}

				if (++i == text.Length)
				{
					error = "A SQL text line ends with an escape character";
					return false;
				}

				switch (text[i])
				{
					case '\\': sb.Append('\\'); break;
					case 't' : sb.Append('\t'); break;
					case 'r' : sb.Append('\r'); break;
					case 'n' : sb.Append('\n'); break;
					case 's' : sb.Append(' ');  break;
					case 'e' : break;
					default:
						error = $"A SQL text line has an unknown escape '\\{text[i]}'";
						return false;
				}
			}

			value = sb.ToString();
			return true;
		}

		/// <summary>
		/// A file counts only under the project directory or a <c>LinqToDBSqlTextRoots</c> directory, and never under the
		/// intermediate output directory. Hygiene, not a security boundary: package build assets are trusted build code.
		/// </summary>
		internal static bool TryGetOrigin(string path, SqlTextUserCatalogGenerator.Settings settings, out string origin)
		{
			origin = path;

			if (string.IsNullOrEmpty(settings.ProjectDir))
				return false;

			var projectDir = Normalize(settings.ProjectDir!, null);
			var full       = Normalize(path, projectDir);

			if (!string.IsNullOrEmpty(settings.IntermediateDir) && IsUnder(full, Normalize(settings.IntermediateDir!, projectDir)))
				return false;

			if (IsUnder(full, projectDir))
			{
				origin = full.Substring(projectDir.Length).TrimStart('/');
				return true;
			}

			foreach (var root in (settings.Roots ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries))
			{
				var rootDir = Normalize(root.Trim(), projectDir);

				if (IsUnder(full, rootDir))
				{
					origin = full;
					return true;
				}
			}

			return false;
		}

		static readonly bool _ignoreCase = Path.DirectorySeparatorChar == '\\';

		// Resolves '.' and '..' segments by hand against base, so a relative path never depends on the compiler's working directory.
		internal static string Normalize(string path, string? baseDir)
		{
			path = path.Replace('\\', '/');

			if (baseDir != null && !IsRooted(path))
				path = baseDir.TrimEnd('/') + "/" + path;

			var segments = new List<string>();
			var parts    = path.Split('/');

			for (var i = 0; i < parts.Length; i++)
			{
				var part = parts[i];

				if (part == "." || (part.Length == 0 && i > 0 && i < parts.Length - 1))
					continue;

				if (part == ".." && segments.Count > 1)
				{
					segments.RemoveAt(segments.Count - 1);
					continue;
				}

				segments.Add(part);
			}

			return string.Join("/", segments).TrimEnd('/');
		}

		static bool IsRooted(string path)
		{
			return path.StartsWith("/", StringComparison.Ordinal) || (path.Length > 1 && path[1] == ':');
		}

		static bool IsUnder(string path, string dir)
		{
			var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

			return path.Length > dir.Length && path.StartsWith(dir, comparison) && path[dir.Length] == '/';
		}
	}
}
