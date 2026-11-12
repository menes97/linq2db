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
				var location = Location.Create(file.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span));
				var prefix   = "Line " + (line.LineNumber + 1).ToString(CultureInfo.InvariantCulture) + " of '" + origin + "': ";

				// the grammar is SqlTextCatalog.Load's, shared through SqlTextLineFormat; a blank or comment line has no error
				if (!SqlTextLineFormat.TryParseEntry(line.ToString(), out var add, out var kind, out var value, out var error))
				{
					if (error != null)
						collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.MalformedLineRule, location, prefix + error));

					continue;
				}

				if (!collector.Kinds.Contains(kind))
				{
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.MalformedLineRule, location, prefix + "'" + kind + "' is not a SqlTextKind name"));
					continue;
				}

				if (!add)
				{
					// GHSA-B-TEMP: removals need the SqlTextItem removal flag; until it lands the line is reported, never dropped silently.
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.UnsupportedRule, location,
						prefix + "removes an entry, which the referenced linq2db cannot express in a generated catalog"));
					continue;
				}

				collector.AddFileEntry(kind, value, origin, location);
			}
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
