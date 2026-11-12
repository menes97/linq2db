using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// Opt-in generator of the SQL text catalog of a user assembly: the constant SQL text its mappings, translators and query
	/// call sites send, which a <c>LinqService</c> with <c>TrustedClient = false</c> otherwise rejects.
	/// Enabled by the MSBuild property <c>LinqToDBSqlTextCatalog=true</c>.
	/// </summary>
	[Generator(LanguageNames.CSharp)]
	public sealed class SqlTextUserCatalogGenerator : IIncrementalGenerator
	{
		internal const string EnabledProperty      = "build_property.LinqToDBSqlTextCatalog";
		internal const string ProjectDirProperty   = "build_property.LinqToDBSqlTextProjectDir";
		internal const string IntermediateProperty = "build_property.LinqToDBSqlTextIntermediateDir";
		internal const string RootsProperty        = "build_property.LinqToDBSqlTextRoots";
		internal const string FileMetadata         = "build_metadata.AdditionalFiles.LinqToDBSqlText";

		const string CatalogAttributeName = "LinqToDB.Remote.SqlTextCatalogAttribute";
		const string CatalogInterfaceName = "LinqToDB.Remote.ISqlTextCatalog";
		const string ItemTypeName         = "LinqToDB.Remote.SqlTextItem";
		const string KindTypeName         = "LinqToDB.Remote.SqlTextKind";

		/// <summary>Test hook: invoked when the generator starts walking a compilation.</summary>
		internal static Action<Compilation>? OnWalk { get; set; }

		internal static readonly DiagnosticDescriptor NotConstantRule = new(
			id:                 "L2DB1005",
			title:              "SQL text is not a compile-time constant",
			messageFormat:      "{0} is not a compile-time constant, so it cannot be catalogued and untrusted LinqService clients sending it are rejected",
			category:           "LinqToDB",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "With LinqToDBSqlTextCatalog enabled, the generated SQL text catalog lists the constant SQL text this project sends. Text built at run time cannot be listed, so a LinqService with TrustedClient = false rejects it. Make the text constant, add it to a LinqToDBSqlText allow file, or add it on the server through LinqService.SqlText.",
			helpLinkUri:        "https://github.com/linq2db/linq2db/wiki/L2DB1005");

		internal static readonly DiagnosticDescriptor QuotedPlaceholderRule = new(
			id:                 "L2DB1006",
			title:              "SQL text with a placeholder inside quotes is not catalogued",
			messageFormat:      "'{0}' has a placeholder inside a quoted context and is not catalogued",
			category:           "LinqToDB",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "A placeholder inside a string literal or a quoted identifier would let a client-chosen value land inside quotes, so such text is never accepted from untrusted clients.",
			helpLinkUri:        "https://github.com/linq2db/linq2db/wiki/L2DB1006");

		internal static readonly DiagnosticDescriptor MalformedLineRule = new(
			id:                 "L2DB1007",
			title:              "Malformed SQL text allow or deny line",
			messageFormat:      "{0}",
			category:           "LinqToDB",
			defaultSeverity:    DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description:        "A LinqToDBSqlText file line is '+' or '-', a SqlTextKind name and the escaped text, separated by tabs; '#' starts a comment.",
			helpLinkUri:        "https://github.com/linq2db/linq2db/wiki/L2DB1007");

		internal static readonly DiagnosticDescriptor FileLocationRule = new(
			id:                 "L2DB1008",
			title:              "SQL text allow or deny file outside the project",
			messageFormat:      "'{0}' is not under the project directory or a LinqToDBSqlTextRoots directory, or is under the intermediate output directory, and is ignored",
			category:           "LinqToDB",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "Only files of the project itself, or under a directory listed in the LinqToDBSqlTextRoots property, contribute to the generated SQL text catalog, so a file added by a package or written into obj/ is not taken silently.",
			helpLinkUri:        "https://github.com/linq2db/linq2db/wiki/L2DB1008");

		internal static readonly DiagnosticDescriptor UnsupportedRule = new(
			id:                 "L2DB1009",
			title:              "Referenced linq2db cannot hold a generated SQL text catalog",
			messageFormat:      "{0}",
			category:           "LinqToDB",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "LinqToDBSqlTextCatalog is enabled, but the referenced linq2db lacks the catalog types this generator targets, so no catalog is generated and untrusted LinqService clients sending this project's SQL text are rejected.",
			helpLinkUri:        "https://github.com/linq2db/linq2db/wiki/L2DB1009");

		/// <inheritdoc/>
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			var settings = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => Settings.Read(provider.GlobalOptions));

			var files = context.AdditionalTextsProvider
				.Combine(context.AnalyzerConfigOptionsProvider)
				.Where(static x => x.Right.GetOptions(x.Left).TryGetValue(FileMetadata, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
				.Select(static (x, _) => x.Left)
				.Collect();

			// The walk runs only here, after the gate: a syntax provider would visit every tree of every consumer of the package.
			context.RegisterImplementationSourceOutput(settings.Combine(context.CompilationProvider).Combine(files), static (spc, x) =>
			{
				var ((options, compilation), texts) = x;

				if (options.Enabled)
					Execute(spc, options, compilation, texts);
			});
		}

		internal sealed record Settings(bool Enabled, string? ProjectDir, string? IntermediateDir, string? Roots)
		{
			public static Settings Read(AnalyzerConfigOptions options)
			{
				options.TryGetValue(EnabledProperty,      out var enabled);
				options.TryGetValue(ProjectDirProperty,   out var projectDir);
				options.TryGetValue(IntermediateProperty, out var intermediateDir);
				options.TryGetValue(RootsProperty,        out var roots);

				return new(string.Equals(enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase), projectDir, intermediateDir, roots);
			}
		}

		static void Execute(SourceProductionContext context, Settings settings, Compilation compilation, ImmutableArray<AdditionalText> files)
		{
			var kindType = compilation.GetTypeByMetadataName(KindTypeName);

			if (compilation.GetTypeByMetadataName(CatalogAttributeName) == null
				|| compilation.GetTypeByMetadataName(CatalogInterfaceName) == null
				|| kindType == null
				|| GetItemConstructor(compilation.GetTypeByMetadataName(ItemTypeName), kindType) is not { } supportsRemoval)
			{
				context.ReportDiagnostic(Diagnostic.Create(UnsupportedRule, Location.None, "LinqToDBSqlTextCatalog is enabled, but the referenced linq2db has no SqlTextCatalogAttribute, ISqlTextCatalog or SqlTextItem(SqlTextKind, string, string, string, bool), so no SQL text catalog is generated"));
				return;
			}

			OnWalk?.Invoke(compilation);

			var collector = new SqlTextCollector(new SqlTextKinds(kindType), context.ReportDiagnostic) { SupportsRemoval = supportsRemoval };

			foreach (var tree in compilation.SyntaxTrees)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				Walk(tree, compilation.GetSemanticModel(tree), collector, context.CancellationToken);
			}

			foreach (var file in files)
				SqlTextFiles.Read(file, settings, collector, context.CancellationToken);

			context.AddSource("LinqToDBSqlTextCatalog.g.cs", SourceText.From(Emit(compilation.AssemblyName, collector.Items), Encoding.UTF8));
		}

		/// <summary>
		/// <see langword="null"/> when <c>SqlTextItem</c> has no <c>(SqlTextKind, string, string, string, bool, ...)</c>
		/// constructor; otherwise whether it takes the trailing <c>bool IsRemoval</c> that removals need.
		/// </summary>
		static bool? GetItemConstructor(INamedTypeSymbol? itemType, INamedTypeSymbol kindType)
		{
			if (itemType == null)
				return null;

			bool? result = null;

			foreach (var c in itemType.InstanceConstructors)
			{
				var p = c.Parameters;

				if (p.Length < 5
					|| !SymbolEqualityComparer.Default.Equals(p[0].Type, kindType)
					|| p[1].Type.SpecialType != SpecialType.System_String
					|| p[2].Type.SpecialType != SpecialType.System_String
					|| p[3].Type.SpecialType != SpecialType.System_String
					|| p[4].Type.SpecialType != SpecialType.System_Boolean
					|| p.Skip(5).Any(static x => !x.HasExplicitDefaultValue))
				{
					continue;
				}

				var removal = p.Length > 5 && p[5].Type.SpecialType == SpecialType.System_Boolean && string.Equals(p[5].Name, "IsRemoval", StringComparison.Ordinal);

				result = result == true || removal;
			}

			return result;
		}

		static void Walk(SyntaxTree tree, SemanticModel model, SqlTextCollector collector, CancellationToken cancellationToken)
		{
			collector.Model = model;

			foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
			{
				switch (node)
				{
					case AttributeSyntax attribute:
						CollectAttribute(attribute, model, collector, cancellationToken);
						break;

					case InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or AssignmentExpressionSyntax:
					{
						var operation = model.GetOperation(node, cancellationToken);

						if (operation == null)
							break;

						collector.Containing = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);

						if (operation is IObjectCreationOperation creation)
							SqlTextAttributeSources.Collect(creation, collector);

						SqlTextCallSites.Collect(operation, collector);
						break;
					}
				}
			}
		}

		static void CollectAttribute(AttributeSyntax attribute, SemanticModel model, SqlTextCollector collector, CancellationToken cancellationToken)
		{
			if (attribute.Parent?.Parent is not { } declaration || model.GetDeclaredSymbol(declaration, cancellationToken) is not { } owner)
				return;

			foreach (var data in owner.GetAttributes())
			{
				if (data.ApplicationSyntaxReference?.GetSyntax(cancellationToken) == attribute)
				{
					SqlTextAttributeSources.Collect(data, owner, collector, attribute.GetLocation());
					break;
				}
			}
		}

		static string Emit(string? assemblyName, IEnumerable<SqlTextItemModel> items)
		{
			var typeName = "SqlTextCatalog_" + Sanitize(assemblyName);
			var sb       = new StringBuilder();

			// C# 7.3 and nullable-oblivious: the consuming project may be an SDK-style net462 project on the default language version.
			sb.AppendLine("// <auto-generated/>");
			sb.Append("[assembly: global::LinqToDB.Remote.SqlTextCatalog(typeof(global::LinqToDBGenerated.").Append(typeName).AppendLine("))]");
			sb.AppendLine();
			sb.AppendLine("namespace LinqToDBGenerated");
			sb.AppendLine("{");
			sb.Append("\tinternal sealed class ").Append(typeName).AppendLine(" : global::LinqToDB.Remote.ISqlTextCatalog");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tstatic readonly global::LinqToDB.Remote.SqlTextItem[] _items =");
			sb.AppendLine("\t\t{");

			foreach (var item in items.OrderBy(static i => i.Kind, StringComparer.Ordinal).ThenBy(static i => i.Text, StringComparer.Ordinal))
			{
				sb.Append("\t\t\tnew global::LinqToDB.Remote.SqlTextItem(global::LinqToDB.Remote.SqlTextKind.").Append(item.Kind).Append(", ")
					.Append(SymbolDisplay.FormatLiteral(item.Text, true)).Append(", null, ")
					.Append(item.Origin == null ? "null" : SymbolDisplay.FormatLiteral(item.Origin, true)).Append(", ")
					.Append(item.IsTemplate ? "true" : "false")
					.Append(item.IsRemoval ? ", true" : "")
					.AppendLine("),");
			}

			sb.AppendLine("\t\t};");
			sb.AppendLine();
			sb.AppendLine("\t\tpublic global::System.Collections.Generic.IReadOnlyList<global::LinqToDB.Remote.SqlTextItem> Items { get { return _items; } }");
			sb.AppendLine("\t}");
			sb.AppendLine("}");

			return sb.ToString();
		}

		internal static string Sanitize(string? assemblyName)
		{
			var sb = new StringBuilder();

			foreach (var c in assemblyName ?? "Assembly")
				sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

			if (sb.Length == 0 || char.IsDigit(sb[0]))
				sb.Insert(0, '_');

			return sb.ToString();
		}
	}
}
