using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
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
			// a '-' line only narrows, so every way of losing one is an error (L2DB1010); a lost '+' line only refuses
			if (file.GetText(cancellationToken) is not { } text)
			{
				collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.DenialNotAppliedRule, Location.None,
					"'" + file.Path + "' cannot be read, so no denial in it is applied"));
				return;
			}

			if (!TryGetOrigin(file.Path, settings, out var origin))
			{
				collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.FileLocationRule, Location.None, file.Path));
				ReportIgnoredDenials(file, text, collector);
				return;
			}

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

				if (add)
					collector.AddFileEntry(kind, value, origin, location);
				else if (collector.SupportsRemoval)
					collector.AddRemoval(kind, value, origin);
				else
					// a linq2db without SqlTextItem.IsRemoval cannot carry the removal
					collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.DenialNotAppliedRule, location,
						prefix + "removes an entry, which the referenced linq2db cannot express in a generated catalog, so the denial is not applied"));
			}
		}

		static void ReportIgnoredDenials(AdditionalText file, SourceText text, SqlTextCollector collector)
		{
			foreach (var line in text.Lines)
			{
				if (!SqlTextLineFormat.TryParseEntry(line.ToString(), out var add, out _, out _, out _) || add)
					continue;

				var location = Location.Create(file.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span));

				collector.Report(Diagnostic.Create(SqlTextUserCatalogGenerator.DenialNotAppliedRule, location,
					"Line " + (line.LineNumber + 1).ToString(CultureInfo.InvariantCulture) + " of '" + file.Path + "' removes an entry, but the file is ignored (L2DB1008), so the denial is not applied"));
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
					// never the absolute path: it would put the build machine's directory into the assembly
					origin = RelativeTo(full, projectDir) ?? full.Substring(rootDir.Length).TrimStart('/');
					return true;
				}
			}

			return false;
		}

		// '../'-relative from the project directory; null when the two share no directory (another drive).
		static string? RelativeTo(string path, string baseDir)
		{
			var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			var target     = path.Split('/');
			var source     = baseDir.Split('/');
			var common     = 0;

			while (common < target.Length - 1 && common < source.Length && string.Equals(target[common], source[common], comparison))
				common++;

			if (common == 0)
				return null;

			var sb = new StringBuilder();

			for (var i = common; i < source.Length; i++)
				sb.Append("../");

			return sb.Append(string.Join("/", target, common, target.Length - common)).ToString();
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

				if (string.Equals(part, ".", StringComparison.Ordinal) || (part.Length == 0 && i > 0 && i < parts.Length - 1))
					continue;

				if (string.Equals(part, "..", StringComparison.Ordinal) && segments.Count > 1)
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
