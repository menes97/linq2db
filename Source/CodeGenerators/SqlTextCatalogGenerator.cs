using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGenerators
{
	/// <summary>
	/// Generates the catalog of SQL text linq2db itself can send, which a <c>LinqService</c> accepts from untrusted clients.
	/// </summary>
	[Generator]
	public sealed class SqlTextCatalogGenerator : IIncrementalGenerator
	{
		const string AssemblyName = "linq2db";

		const int ExpressionKind        = 0;
		const int FunctionKind          = 2;
		const int TableExpressionKind   = 3;
		const int ExtensionArgumentKind = 6;

		static readonly string[] KindNames =
		[
			"Expression", "Fragment", "Function", "TableExpression", "Operator", "QueryHint",
			"ExtensionArgument", "MergeHint", "RawSqlTable", "StatementHeader", "StatementFooter", "TableName",
		];

		readonly record struct Item(int Kind, string Text, string? Partition, string? Origin, bool IsTemplate);

		/// <inheritdoc/>
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			var items = context.SyntaxProvider
				.CreateSyntaxProvider(IsCandidate, Transform)
				.SelectMany(static (x, _) => x)
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

			var expected = new HashSet<string>(StringComparer.Ordinal);

			foreach (var text in baselines)
				foreach (var line in (text ?? "").Split('\n'))
					if (line.TrimEnd() is { Length: > 0 } entry && entry[0] != '#')
						expected.Add(entry);

			var actual = new HashSet<string>(generated.Select(FormatBaselineLine), StringComparer.Ordinal);

			var missing = actual.Where(l => !expected.Contains(l)).OrderBy(static l => l, StringComparer.Ordinal).ToList();
			var stale   = expected.Where(l => !actual.Contains(l)).OrderBy(static l => l, StringComparer.Ordinal).ToList();

			var reported = 0;

			foreach (var line in missing)
				if (reported++ < MaxReported)
					context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"Generated entry missing from {BaselineFileName}, add it if the change is intended: {line}"));

			foreach (var line in stale)
				if (reported++ < MaxReported)
					context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"{BaselineFileName} entry no longer generated, remove it if the change is intended: {line}"));

			if (reported > MaxReported)
				context.ReportDiagnostic(Diagnostic.Create(BaselineRule, Location.None, $"{reported - MaxReported} more {BaselineFileName} differences are not listed."));
		}

		// <kind> TAB <partition or -> TAB <text>, with backslash, tab, CR and LF escaped. A line never ends in whitespace,
		// which editors and the build log strip: trailing spaces are written as \s and an empty text as \e.
		static string FormatBaselineLine(Item item)
		{
			var text = item.Text
				.Replace("\\", "\\\\")
				.Replace("\t", "\\t")
				.Replace("\r", "\\r")
				.Replace("\n", "\\n");

			var trimmed = text.TrimEnd(' ');

			text = text.Length == 0 ? "\\e" : trimmed + string.Concat(Enumerable.Repeat("\\s", text.Length - trimmed.Length));

			return KindNames[item.Kind] + "\t" + (item.Partition ?? "-") + "\t" + text;
		}

		static bool IsCandidate(SyntaxNode node, System.Threading.CancellationToken cancellationToken)
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

		static ImmutableArray<Item> Transform(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken)
		{
			if (context.SemanticModel.Compilation.AssemblyName != AssemblyName)
				return [];

			return context.Node switch
			{
				AttributeSyntax attribute         => TransformAttribute(attribute, context.SemanticModel),
				FieldDeclarationSyntax field      => TransformHintConstant(field, context.SemanticModel),
				PropertyDeclarationSyntax property => TransformProperty(property, context.SemanticModel),
				_                              => TransformCall(context.Node, context.SemanticModel, cancellationToken),
			};
		}

		static ImmutableArray<Item> TransformCall(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
		{
			var operation = model.GetOperation(node, cancellationToken);

			if (operation == null)
				return [];

			var result     = ImmutableArray.CreateBuilder<Item>();
			var containing = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);

			foreach (var (argument, kind) in SqlTextFlow.GetSqlTextArguments(operation))
				AddValues(result, argument.Value, kind, containing, model);

			if (operation is Microsoft.CodeAnalysis.Operations.ISimpleAssignmentOperation assignment && SqlTextFlow.TryGetAssignedKind(assignment, out var assignedKind))
				AddValues(result, assignment.Value, assignedKind, containing, model);

			return result.ToImmutable();
		}

		static void AddValues(ImmutableArray<Item>.Builder result, IOperation value, int kind, ISymbol? containing, SemanticModel model)
		{
			var values = new List<string>();

			SqlTextFlow.TryResolve(value, model, values);

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

		static ImmutableArray<Item> TransformAttribute(AttributeSyntax attribute, SemanticModel model)
		{
			if (model.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor)
				return [];

			if (IsSqlAttribute(constructor.ContainingType, "QueryExtensionAttribute"))
				return TransformQueryExtension(attribute, constructor, model);

			var attributeKind = GetAttributeKind(constructor.ContainingType);

			if (attributeKind == null)
				return [];

			var owner = model.GetDeclaredSymbol(attribute.Parent!.Parent!);
			var text  = GetAttributeText(attribute, constructor, model) ?? (attributeKind == ExpressionKind ? null : owner?.Name);

			if (text == null || text.Length == 0)
				return [];

			return [new Item(attributeKind.Value, text, GetPartition(owner), owner?.ContainingType?.Name, attributeKind == ExpressionKind)];
		}

		static bool IsSqlAttribute(INamedTypeSymbol? type, string name)
		{
			return type is { ContainingType.Name: "Sql" }
				&& type.ContainingType.ContainingNamespace?.ToDisplayString() == "LinqToDB"
				&& type.Name == name;
		}

		/// <summary>
		/// Catalogues the <c>extensionArgument</c> texts of a <c>[Sql.QueryExtension]</c>, which reach the builder as
		/// <c>.ExtensionArguments.{n}</c>; a params array is passed as the trailing positional arguments.
		/// </summary>
		static ImmutableArray<Item> TransformQueryExtension(AttributeSyntax attribute, IMethodSymbol constructor, SemanticModel model)
		{
			if (attribute.ArgumentList == null || constructor.Parameters.IsEmpty)
				return [];

			var owner  = model.GetDeclaredSymbol(attribute.Parent!.Parent!);
			var result = ImmutableArray.CreateBuilder<Item>();
			var last   = constructor.Parameters[constructor.Parameters.Length - 1];
			var index  = 0;

			foreach (var argument in attribute.ArgumentList.Arguments)
			{
				if (argument.NameEquals != null || argument.NameColon != null)
					continue;

				var parameter = index < constructor.Parameters.Length ? constructor.Parameters[index] : last.IsParams ? last : null;

				index++;

				if (parameter == null || !parameter.Name.StartsWith("extensionArgument", StringComparison.Ordinal))
					continue;

				if (model.GetConstantValue(argument.Expression) is { HasValue: true, Value: string text })
					result.Add(new Item(ExtensionArgumentKind, text, GetPartition(owner), owner?.ContainingType?.Name, false));
			}

			return result.ToImmutable();
		}

		static int? GetAttributeKind(INamedTypeSymbol? type)
		{
			for (; type != null; type = type.BaseType)
			{
				if (type.ContainingType?.Name != "Sql" || type.ContainingType.ContainingNamespace?.ToDisplayString() != "LinqToDB")
					continue;

				switch (type.Name)
				{
					case "FunctionAttribute"        : return FunctionKind;
					case "TableExpressionAttribute" : return TableExpressionKind;
					case "TableFunctionAttribute"   : return FunctionKind;
					case "PropertyAttribute"        : return ExpressionKind;
					case "ExtensionAttribute"       : return ExpressionKind;
					case "ExpressionAttribute"      : return ExpressionKind;
				}
			}

			return null;
		}

		static string? GetAttributeText(AttributeSyntax attribute, IMethodSymbol constructor, SemanticModel model)
		{
			if (attribute.ArgumentList == null)
				return null;

			var position = 0;

			foreach (var argument in attribute.ArgumentList.Arguments)
			{
				if (argument.NameEquals != null)
				{
					if (argument.NameEquals.Name.Identifier.Text is "Name" or "Expression" && model.GetConstantValue(argument.Expression) is { HasValue: true, Value: string named })
						return named;

					continue;
				}

				if (position < constructor.Parameters.Length
					&& constructor.Parameters[position].Name is "name" or "expression"
					&& model.GetConstantValue(argument.Expression) is { HasValue: true, Value: string value })
				{
					return value;
				}

				position++;
			}

			return null;
		}

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

			foreach (var item in items.Distinct().OrderBy(static i => i.Kind).ThenBy(static i => i.Text, StringComparer.Ordinal).ThenBy(static i => i.Partition, StringComparer.Ordinal))
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
