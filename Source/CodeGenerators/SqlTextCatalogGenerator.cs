using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGenerators
{
	/// <summary>
	/// Generates the catalog of SQL text linq2db itself can send, which a <c>LinqService</c> accepts from untrusted clients.
	/// </summary>
	[Generator]
	public sealed class SqlTextCatalogGenerator : IIncrementalGenerator
	{
		const string AssemblyName = "linq2db";

		// values of LinqToDB.Remote.SqlTextKind; new members are appended there, so these stay stable
		const int ExpressionKind        = 0;
		const int QueryHintKind         = 5;
		const int ExtensionArgumentKind = 6;
		const int RawSqlTableKind       = 8;
		const int DbTypeKind            = 12;

		static readonly string[] KindNames =
		[
			"Expression", "Fragment", "Function", "TableExpression", "Operator", "QueryHint",
			"ExtensionArgument", "MergeHint", "RawSqlTable", "StatementHeader", "StatementFooter", "TableName", "DbType",
		];

		internal readonly record struct Item(int Kind, string Text, string? Partition, string? Origin, bool IsTemplate);

		// an incremental pipeline caches by value: ImmutableArray compares by reference, so it would never hit
		readonly struct EquatableItems(ImmutableArray<Item> items) : IEquatable<EquatableItems>
		{
			public ImmutableArray<Item> Items { get; } = items.IsDefault ? ImmutableArray<Item>.Empty : items;

			public bool Equals(EquatableItems other) => Items.SequenceEqual(other.Items);
			public override bool Equals(object? obj) => obj is EquatableItems other && Equals(other);

			public override int GetHashCode()
			{
				var hash = 17;

				foreach (var item in Items)
					hash = unchecked(hash * 31 + item.GetHashCode());

				return hash;
			}
		}

		/// <inheritdoc/>
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			var items = context.SyntaxProvider
				.CreateSyntaxProvider(IsCandidate, Transform)
				.SelectMany(static (x, _) => x.Items)
				.Collect();

			var enabled = context.CompilationProvider.Select(static (c, _) => c.AssemblyName == AssemblyName);

			var baseline = context.AdditionalTextsProvider
				.Where(static t => string.Equals(System.IO.Path.GetFileName(t.Path), BaselineFileName, StringComparison.OrdinalIgnoreCase))
				.Select(static (t, ct) => t.GetText(ct)?.ToString())
				.Collect();

			context.RegisterImplementationSourceOutput(items.Combine(enabled).Combine(baseline), static (spc, x) =>
			{
				var ((generated, isEnabled), baselines) = x;

				if (!isEnabled)
					return;

				spc.AddSource("SqlTextCatalog.g.cs", Emit(generated));
				CheckBaseline(spc, generated, baselines);
			});
		}

		const string BaselineFileName = "SqlTextCatalog.baseline.txt";
		const int    MaxReported      = 50;

		static readonly DiagnosticDescriptor BaselineRule = new(
			id:                 "LINQ2DB0008",
			title:              "SQL text catalog differs from its baseline",
			messageFormat:      "{0}",
			category:           "Usage",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "The generated SQL text catalog decides what untrusted LinqService clients may send, so every change to it is reviewed through PublicAPI/" + BaselineFileName + " (shared) and PublicAPI/<tfm>/" + BaselineFileName + " (entries of one target framework).");

		/// <summary>
		/// Compares the generated entries with the committed baseline files, so a change to what untrusted clients may send
		/// shows up in review and fails the build until the baseline is updated.
		/// </summary>
		static void CheckBaseline(SourceProductionContext context, ImmutableArray<Item> generated, ImmutableArray<string?> baselines)
		{
			if (baselines.IsDefaultOrEmpty)
			{
				context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"No {BaselineFileName} is passed as an additional file."));
				return;
			}

			var expected   = new HashSet<string>(StringComparer.Ordinal);
			var duplicates = new List<string>();

			foreach (var text in baselines)
			{
				// a line never ends in whitespace (SqlTextLineFormat), so only the CR of a CRLF file is trimmed
				foreach (var line in (text ?? "").Split('\n'))
					if (line.TrimEnd('\r') is { Length: > 0 } entry && entry[0] != '#' && !expected.Add(entry))
						duplicates.Add(entry);
			}

			var actual = new HashSet<string>(generated.Select(FormatBaselineLine), StringComparer.Ordinal);

			var missing = actual.Where(l => !expected.Contains(l)).OrderBy(static l => l, StringComparer.Ordinal).ToList();
			var stale   = expected.Where(l => !actual.Contains(l)).OrderBy(static l => l, StringComparer.Ordinal).ToList();

			var reported = 0;

			foreach (var line in duplicates)
				if (reported++ < MaxReported)
					context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"Entry listed twice across the {BaselineFileName} files, keep it in one: {line}"));

			foreach (var line in missing)
				if (reported++ < MaxReported)
					context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"Generated entry missing from {BaselineFileName}, add it if the change is intended: {line}"));

			foreach (var line in stale)
				if (reported++ < MaxReported)
					context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"{BaselineFileName} entry no longer generated, remove it if the change is intended: {line}"));

			if (reported > MaxReported)
				context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"{reported - MaxReported} more {BaselineFileName} differences are not listed."));
		}

		// <kind> TAB <partition or -> TAB <escaped text>; the escaping is shared with SqlTextCatalog.Export and Load
		static string FormatBaselineLine(Item item)
		{
			return LinqToDB.Analyzers.SqlTextLineFormat.FormatBaseline(KindNames[item.Kind], item.Partition, item.Text);
		}

		internal static bool IsCandidate(SyntaxNode node, System.Threading.CancellationToken cancellationToken)
		{
			return node switch
			{
				ObjectCreationExpressionSyntax         => true,
				ImplicitObjectCreationExpressionSyntax => true,
				InvocationExpressionSyntax             => true,
				AssignmentExpressionSyntax             => true,
				AttributeSyntax                        => true,
				PropertyDeclarationSyntax property     => property.ExpressionBody != null,
				FieldDeclarationSyntax field           => field.Modifiers.Any(SyntaxKind.ConstKeyword),
				_                                      => false,
			};
		}

		static EquatableItems Transform(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken)
		{
			if (context.SemanticModel.Compilation.AssemblyName != AssemblyName)
				return default;

			// a text refused for its provider's quoting can never be accepted from a client; the analyzer reports its site
			return new(TransformNode(context.Node, context.SemanticModel, cancellationToken).Where(static i => !IsRefused(i)).ToImmutableArray());
		}

		/// <summary>Gets the catalog entries one syntax node of the library contributes. Shared with <see cref="SqlTextContractAnalyzer"/>.</summary>
		internal static ImmutableArray<Item> TransformNode(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
		{
			return node switch
			{
				AttributeSyntax attribute          => TransformAttribute(attribute, model),
				FieldDeclarationSyntax field       => TransformHintConstant(field, model),
				PropertyDeclarationSyntax property => TransformProperty(property, model),
				_                                  => TransformCall(node, model, cancellationToken),
			};
		}

		/// <summary>Whether the text has a format item in a quoted context of its partition's provider.</summary>
		internal static bool IsRefused(Item item)
		{
			var text = item.Kind == QueryHintKind ? item.Text + "\n" : item.Text;

			return LinqToDB.Analyzers.SqlTextQuoting.TryFindQuotedToken(text, LinqToDB.Analyzers.SqlTextQuoting.GetDialect(item.Partition), out _);
		}

		static ImmutableArray<Item> TransformCall(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
		{
			var operation = model.GetOperation(node, cancellationToken);

			if (operation == null)
				return [];

			// a raw statement is built and run by the server alone (DML services' DDL); the validator rejects it from clients
			if (IsRawStatementArgument(operation))
				return [];

			var result     = ImmutableArray.CreateBuilder<Item>();
			var containing = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);

			foreach (var (argument, kind) in SqlTextFlow.GetSqlTextArguments(operation))
				AddValues(result, argument.Value, kind, containing, model);

			// new Sql.*Attribute(...), as fluent mapping creates it, read like the declared attribute
			if (operation is IObjectCreationOperation creation)
				foreach (var (kindName, value) in LinqToDB.Analyzers.SqlTextAttributeSources.GetTextValues(creation))
					if (Array.IndexOf(KindNames, kindName) is var kind and >= 0)
						AddValues(result, value, kind, containing, model);

			switch (operation)
			{
				case ISimpleAssignmentOperation assignment when SqlTextFlow.TryGetAssignedKind(assignment, out var assignedKind):
					AddValues(result, assignment.Value, assignedKind, containing, model);
					break;

				case ICoalesceAssignmentOperation { Target: IPropertyReferenceOperation target } coalesce when SqlTextFlow.TryGetPropertyKind(target.Property, out var coalescedKind):
					AddValues(result, coalesce.Value, coalescedKind, containing, model);
					break;
			}

			AddDbTypeLiteral(result, operation, containing);
			AddInterpolatedSql(result, operation, containing);

			return result.ToImmutable();
		}

		// Sql.Expr and FromSql take user text, so they carry no [SqlText]. The library's own calls pass an interpolated string,
		// which reaches the server as its composite format: that format is the catalog entry.
		static void AddInterpolatedSql(ImmutableArray<Item>.Builder result, IOperation operation, ISymbol? containing)
		{
			if (operation is not IInvocationOperation { TargetMethod: var method } invocation)
				return;

			var kind = method switch
			{
				{ Name: "Expr",    ContainingType: { Name: "Sql",            ContainingNamespace.Name: "LinqToDB" } } => ExpressionKind,
				{ Name: "FromSql", ContainingType: { Name: "DataExtensions", ContainingNamespace.Name: "LinqToDB" } } => RawSqlTableKind,
				_                                                                                                       => -1,
			};

			if (kind < 0)
				return;

			foreach (var argument in invocation.Arguments)
			{
				var value = argument.Value;

				while (value is IConversionOperation conversion)
					value = conversion.Operand;

				if (argument.Parameter?.Type.Name == "FormattableString" && value is IInterpolatedStringOperation interpolated && TryGetCompositeFormat(interpolated, out var format))
					result.Add(new Item(kind, format, GetPartition(containing), containing?.ContainingType?.Name, false));
			}
		}

		// the format FormattableStringFactory receives: literal braces doubled, each hole numbered in order
		static bool TryGetCompositeFormat(IInterpolatedStringOperation interpolated, out string format)
		{
			var sb   = new StringBuilder();
			var hole = 0;

			foreach (var part in interpolated.Parts)
			{
				switch (part)
				{
					case IInterpolatedStringTextOperation { Text.ConstantValue.Value: string text }:
						sb.Append(text.Replace("{", "{{").Replace("}", "}}"));
						break;

					case IInterpolationOperation { Alignment: null, FormatString: null }:
						sb.Append('{').Append(hole++).Append('}');
						break;

					default:
						format = "";
						return false;
				}
			}

			format = sb.ToString();
			return true;
		}

		static bool IsRawStatementArgument(IOperation operation)
		{
			return operation.Parent is IArgumentOperation { Parent: IObjectCreationOperation { Type.Name: "SqlRawStatement" } };
		}

		// a constant DbType given to DbDataType is rendered as written in casts and DDL
		static void AddDbTypeLiteral(ImmutableArray<Item>.Builder result, IOperation operation, ISymbol? containing)
		{
			var (method, arguments) = operation switch
			{
				IInvocationOperation     invocation => (invocation.TargetMethod, invocation.Arguments),
				IObjectCreationOperation creation   => (creation.Constructor, creation.Arguments),
				_                                   => (null, default),
			};

			if (method?.ContainingType is not { Name: "DbDataType", ContainingNamespace.Name: "LinqToDB" } || arguments.IsDefaultOrEmpty)
				return;

			if (method.MethodKind != MethodKind.Constructor && !string.Equals(method.Name, "WithDbType", StringComparison.Ordinal))
				return;

			foreach (var argument in arguments)
			{
				if (string.Equals(argument.Parameter?.Name, "dbType", StringComparison.Ordinal)
					&& argument.Value.ConstantValue is { HasValue: true, Value: string { Length: > 0 } dbType })
				{
					result.Add(new Item(DbTypeKind, dbType, GetPartition(containing), containing?.ContainingType?.Name, false));
				}
			}
		}

		static void AddValues(ImmutableArray<Item>.Builder result, IOperation value, int kind, ISymbol? containing, SemanticModel model)
		{
			var values = new List<string>();

			SqlTextFlow.TryResolve(value, model, values, 0, kind);

			var partition = GetPartition(containing);
			var origin    = containing?.ContainingType?.Name;

			foreach (var text in values)
				result.Add(new Item(kind, text, partition, origin, kind == ExpressionKind && HasNamedToken(text)));
		}

		static bool HasNamedToken(string text)
		{
			for (var i = 0; i + 1 < text.Length; i++)
				if (text[i] == '{' && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
					return true;

			return false;
		}

		static ImmutableArray<Item> TransformProperty(PropertyDeclarationSyntax property, SemanticModel model)
		{
			if (model.GetDeclaredSymbol(property) is not IPropertySymbol symbol || !SqlTextFlow.TryGetPropertyKind(symbol, out var kind))
				return [];

			var result = ImmutableArray.CreateBuilder<Item>();

			AddValues(result, model.GetOperation(property.ExpressionBody!.Expression)!, kind, symbol, model);

			return result.ToImmutable();
		}

		static ImmutableArray<Item> TransformHintConstant(FieldDeclarationSyntax field, SemanticModel model)
		{
			var result = ImmutableArray.CreateBuilder<Item>();

			foreach (var variable in field.Declaration.Variables)
			{
				if (model.GetDeclaredSymbol(variable) is not IFieldSymbol { HasConstantValue: true, ConstantValue: string value } symbol)
					continue;

				for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
				{
					if (type.Name.EndsWith("Hints", StringComparison.Ordinal))
					{
						result.Add(new Item(ExtensionArgumentKind, value, GetPartition(symbol), type.Name, false));
						break;
					}
				}
			}

			return result.ToImmutable();
		}

		// The texts are read by SqlTextAttributeSources, shared with the user catalog generator, so both catalogues read an
		// attribute alike; this maps its kind names and provider configuration to library entries.
		static ImmutableArray<Item> TransformAttribute(AttributeSyntax attribute, SemanticModel model)
		{
			// an assembly or module attribute is applied to no member
			if (attribute.Parent?.Parent is not { } declaration || model.GetDeclaredSymbol(declaration) is not { } owner)
				return [];

			var data = owner.GetAttributes().FirstOrDefault(a =>
				a.ApplicationSyntaxReference is { } reference && reference.SyntaxTree == attribute.SyntaxTree && reference.Span == attribute.Span);

			if (data == null)
				return [];

			var result = ImmutableArray.CreateBuilder<Item>();
			var origin = owner.ContainingType?.Name;

			foreach (var (kindName, text, configuration) in LinqToDB.Analyzers.SqlTextAttributeSources.GetTexts(data, owner))
			{
				var kind = Array.IndexOf(KindNames, kindName);

				if (kind < 0)
					continue;

				// an attribute naming a provider configuration applies to that provider only; an unknown name keeps the text out
				// of every provider partition, and the analyzer reports it
				var partition = configuration == null
					? GetPartition(owner)
					: TryGetConfigurationFamily(configuration, out var family) ? family : UnknownPartition;

				result.Add(new Item(kind, text, partition, origin, kind == ExpressionKind && HasNamedToken(text)));
			}

			return result.ToImmutable();
		}

		// provider names whose first segment is not the provider's namespace family
		static readonly Dictionary<string,string> _configurationFamilies = new(StringComparer.OrdinalIgnoreCase)
		{
			["Access"]     = "Access",     ["ClickHouse"] = "ClickHouse", ["DB2"]      = "DB2",      ["DuckDB"]    = "DuckDB",
			["Firebird"]   = "Firebird",   ["Informix"]   = "Informix",   ["MariaDB"]  = "MySql",    ["MySql"]     = "MySql",
			["MySqlConnector"] = "MySql",  ["Oracle"]     = "Oracle",     ["PostgreSQL"] = "PostgreSQL", ["SapHana"] = "SapHana",
			["SqlCe"]      = "SqlCe",      ["SQLite"]     = "SQLite",     ["SqlServer"] = "SqlServer", ["Sybase"]  = "Sybase",
			["YDB"]        = "Ydb",
		};

		/// <summary>Maps a provider configuration name to its provider family, the namespace segment the runtime partitions by.</summary>
		internal static bool TryGetConfigurationFamily(string configuration, out string family)
		{
			var dot = configuration.IndexOf('.');

			return _configurationFamilies.TryGetValue(dot < 0 ? configuration : configuration.Substring(0, dot), out family!);
		}

		/// <summary>Partition of a text whose provider configuration maps to no provider family. No provider reads it.</summary>
		internal const string UnknownPartition = "?";

		static string? GetPartition(ISymbol? symbol)
		{
			var parts = symbol?.ContainingNamespace?.ToDisplayString().Split('.');

			if (parts == null)
				return null;

			var index = Array.IndexOf(parts, "DataProvider");

			if (index < 0 || index + 1 >= parts.Length || parts[index + 1] == "Translation")
				return null;

			return parts[index + 1];
		}

		static string Emit(ImmutableArray<Item> items)
		{
			var sb = new StringBuilder();

			sb.AppendLine("// <auto-generated/>");
			sb.AppendLine("#nullable enable");
			sb.AppendLine("[assembly: LinqToDB.Remote.SqlTextCatalog(typeof(LinqToDB.Remote.GeneratedSqlTextCatalog))]");
			sb.AppendLine("namespace LinqToDB.Remote");
			sb.AppendLine("{");
			sb.AppendLine("\tsealed class GeneratedSqlTextCatalog : ISqlTextCatalog");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tpublic System.Collections.Generic.IReadOnlyList<SqlTextItem> Items { get; } = new SqlTextItem[]");
			sb.AppendLine("\t\t{");

			// one entry per kind, partition and text: entries differing only by origin collapse to the first origin in order
			var unique = items
				.GroupBy(static i => (i.Kind, i.Partition, i.Text, i.IsTemplate))
				.Select(static g => g.OrderBy(static i => i.Origin, StringComparer.Ordinal).First());

			foreach (var item in unique.OrderBy(static i => i.Kind).ThenBy(static i => i.Text, StringComparer.Ordinal).ThenBy(static i => i.Partition, StringComparer.Ordinal))
			{
				sb.Append("\t\t\tnew(SqlTextKind.").Append(KindNames[item.Kind]).Append(", ")
					.Append(SymbolDisplay.FormatLiteral(item.Text, true)).Append(", ")
					.Append(item.Partition == null ? "null" : SymbolDisplay.FormatLiteral(item.Partition, true)).Append(", ")
					.Append(item.Origin == null ? "null" : SymbolDisplay.FormatLiteral(item.Origin, true)).Append(", ")
					.Append(item.IsTemplate ? "true" : "false").AppendLine("),");
			}

			sb.AppendLine("\t\t};");
			sb.AppendLine("\t}");
			sb.AppendLine("}");

			return sb.ToString();
		}
	}
}
