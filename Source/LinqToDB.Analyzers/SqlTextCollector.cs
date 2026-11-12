using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace LinqToDB.Analyzers
{
	/// <summary>Collects entries and reports the sites whose text cannot be resolved.</summary>
	sealed class SqlTextCollector
	{
		readonly Dictionary<(string Kind, string Text), SqlTextItemModel> _items    = new();
		readonly Dictionary<(string Kind, string Text), SqlTextItemModel> _removals = new();
		readonly Action<Diagnostic>                                        _report;

		public SqlTextCollector(SqlTextKinds kinds, Action<Diagnostic> report)
		{
			Kinds   = kinds;
			_report = report;
		}

		public SqlTextKinds  Kinds           { get; }
		public SemanticModel Model           { get; set; } = null!;
		public ISymbol?      Containing      { get; set; }
		public bool          SupportsRemoval { get; set; }

		public IEnumerable<SqlTextItemModel> Items => _items.Values.Concat(_removals.Values);

		/// <summary>
		/// Adds a deny-file removal. It only ever narrows what clients may send, so it is not subject to the quoting refusal;
		/// the runtime applies it to every source of the service, LinqToDB included.
		/// </summary>
		public void AddRemoval(string kind, string text, string origin)
		{
			var key = (kind, text);

			if (!_removals.ContainsKey(key))
				_removals.Add(key, new SqlTextItemModel(kind, text, origin, false, IsRemoval: true));
		}

		/// <summary>Adds a known text; <paramref name="owner"/> names its origin.</summary>
		public void Add(string kind, string text, ISymbol? owner, Location location)
		{
			if (Kinds.Contains(kind) && !IsRefused(text, location))
				AddItem(kind, text, GetOrigin(owner), string.Equals(kind, "Expression", StringComparison.Ordinal) && HasNamedToken(text));
		}

		// Only the contexts every dialect quotes are refused here; brackets, backticks and dollar quotes depend on the
		// provider family and are checked by the server against its dialect (a PostgreSQL ARRAY[{0}] is legitimate).
		bool IsRefused(string text, Location location)
		{
			if (!SqlTextQuoting.TryFindQuotedToken(text, SqlTextQuoting.Dialect.AlwaysOn, out _))
				return false;

			_report(Diagnostic.Create(SqlTextUserCatalogGenerator.QuotedPlaceholderRule, location, text));
			return true;
		}

		void AddItem(string kind, string text, string? origin, bool isTemplate)
		{
			var key = (kind, text);

			if (!_items.ContainsKey(key))
				_items.Add(key, new SqlTextItemModel(kind, text, origin, isTemplate));
		}

		/// <summary>Adds every text <paramref name="value"/> can produce, or reports it as not constant.</summary>
		public void AddResolved(string kind, IOperation value, Location location)
		{
			var values = new List<string>();

			if (!CodeGenerators.SqlTextFlow.TryResolve(value, Model, values))
			{
				ReportNotConstant(value, location);
				return;
			}

			foreach (var text in values)
				Add(kind, text, Containing, location);
		}

		public void AddValues(string kind, IEnumerable<string> values, Location location)
		{
			foreach (var text in values)
				Add(kind, text, Containing, location);
		}

		public void ReportNotConstant(IOperation value, Location location)
		{
			_report(Diagnostic.Create(SqlTextUserCatalogGenerator.NotConstantRule, location, Describe(value)));
		}

		public void Report(Diagnostic diagnostic) => _report(diagnostic);

		/// <summary>Adds an allow-file entry, subject to the same quoting refusal as text in code.</summary>
		public void AddFileEntry(string kind, string text, string origin, Location location)
		{
			if (!IsRefused(text, location))
				AddItem(kind, text, origin, false);
		}

		static string Describe(IOperation value)
		{
			var text = value.Syntax.ToString();

			return text.Length <= 60 ? "'" + text + "'" : "'" + text.Substring(0, 57) + "...'";
		}

		static string? GetOrigin(ISymbol? owner)
		{
			var type = owner as INamedTypeSymbol ?? owner?.ContainingType;

			return type?.ToDisplayString();
		}

		static bool HasNamedToken(string text)
		{
			for (var i = 0; i + 1 < text.Length; i++)
				if (text[i] == '{' && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
					return true;

			return false;
		}
	}
}
