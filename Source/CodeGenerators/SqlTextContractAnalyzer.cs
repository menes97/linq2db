using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGenerators
{
	/// <summary>
	/// Reports SQL text the catalog generator cannot determine, and library text it leaves out of the catalog. An uncatalogued
	/// text fails at runtime, and only for untrusted remote clients, so without this rule a new translator would pass every
	/// test and break in production.
	/// </summary>
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class SqlTextContractAnalyzer : DiagnosticAnalyzer
	{
		static readonly DiagnosticDescriptor Rule = new(
			id:                 "LINQ2DB0007",
			title:              "SQL text must be known at compile time",
			messageFormat:      "The value passed to '{0}' cannot be catalogued: use a constant, a [SqlText] parameter of the same kind, or a local or field assigned only such values",
			category:           "Usage",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "Text passed to a [SqlText] position is accepted from untrusted LinqService clients only when it is in the generated catalog.");

		static readonly DiagnosticDescriptor QuotedRule = new(
			id:                 "LINQ2DB0009",
			title:              "SQL text has a format item inside a quoted literal or a comment",
			messageFormat:      "'{0}' has a format item inside a quoted literal, quoted identifier or comment of its provider, or leaves one open; it is left out of the catalog, so untrusted clients cannot send it",
			category:           "Usage",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "A rendered argument is not delimited inside a quoted context. Pass the whole literal as one argument instead, or suppress the rule where the text stays rejected for untrusted clients.");

		static readonly DiagnosticDescriptor ConfigurationRule = new(
			id:                 "LINQ2DB0010",
			title:              "SQL text names an unknown provider configuration",
			messageFormat:      "Configuration '{0}' maps to no provider family, so no provider accepts this text from untrusted clients",
			category:           "Usage",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "Catalog entries of an attribute with a configuration are partitioned by the configuration's provider family. Add the provider name to the generator's family table.");

		/// <inheritdoc/>
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, QuotedRule, ConfigurationRule];

		/// <inheritdoc/>
		public override void Initialize(AnalysisContext context)
		{
			context.EnableConcurrentExecution();
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

			context.RegisterOperationAction(AnalyzeCall,                 OperationKind.Invocation, OperationKind.ObjectCreation);
			context.RegisterOperationAction(AnalyzeAssignment,           OperationKind.SimpleAssignment);
			context.RegisterOperationAction(AnalyzeCoalesceAssignment,   OperationKind.CoalesceAssignment);
			context.RegisterOperationAction(AnalyzeComposedAssignment,   OperationKind.CompoundAssignment, OperationKind.DeconstructionAssignment);

			context.RegisterSyntaxNodeAction(AnalyzeCatalogSite,
				SyntaxKind.Attribute,
				SyntaxKind.InvocationExpression,
				SyntaxKind.ObjectCreationExpression,
				SyntaxKind.ImplicitObjectCreationExpression,
				SyntaxKind.SimpleAssignmentExpression,
				SyntaxKind.CoalesceAssignmentExpression,
				SyntaxKind.FieldDeclaration,
				SyntaxKind.PropertyDeclaration);
		}

		static void AnalyzeCall(OperationAnalysisContext context)
		{
			foreach (var (argument, kind) in SqlTextFlow.GetSqlTextArguments(context.Operation))
				Report(context, argument.Value, argument.Parameter!.Name, kind);
		}

		static void AnalyzeAssignment(OperationAnalysisContext context)
		{
			var assignment = (ISimpleAssignmentOperation)context.Operation;

			if (SqlTextFlow.TryGetAssignedKind(assignment, out var kind))
				Report(context, assignment.Value, ((IPropertyReferenceOperation)assignment.Target).Property.Name, kind);
		}

		static void AnalyzeCoalesceAssignment(OperationAnalysisContext context)
		{
			var assignment = (ICoalesceAssignmentOperation)context.Operation;

			if (assignment.Target is IPropertyReferenceOperation target && SqlTextFlow.TryGetPropertyKind(target.Property, out var kind))
				Report(context, assignment.Value, target.Property.Name, kind);
		}

		// `+=` on a property, or a deconstruction into one, composes text from the property's earlier value, which no
		// site shows: the result cannot be catalogued
		static void AnalyzeComposedAssignment(OperationAnalysisContext context)
		{
			var target = context.Operation switch
			{
				ICompoundAssignmentOperation compound          => compound.Target,
				IDeconstructionAssignmentOperation deconstruct => deconstruct.Target,
				_                                              => null,
			};

			foreach (var property in GetProperties(target))
				if (SqlTextFlow.TryGetPropertyKind(property.Property, out _))
					context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), property.Property.Name));
		}

		static IEnumerable<IPropertyReferenceOperation> GetProperties(IOperation? target)
		{
			switch (target)
			{
				case IPropertyReferenceOperation property:
					yield return property;
					break;

				case ITupleOperation tuple:
					foreach (var element in tuple.Elements)
						foreach (var property in GetProperties(element))
							yield return property;
					break;
			}
		}

		static void Report(OperationAnalysisContext context, IOperation value, string target, int kind)
		{
			if (value.SemanticModel == null)
				return;

			if (!SqlTextFlow.TryResolve(value, value.SemanticModel, new List<string>(), 0, kind))
				context.ReportDiagnostic(Diagnostic.Create(Rule, value.Syntax.GetLocation(), target));
		}

		static void AnalyzeCatalogSite(SyntaxNodeAnalysisContext context)
		{
			// the generator transforms only its candidates, and its transforms rely on that
			if (!SqlTextCatalogGenerator.IsCandidate(context.Node, context.CancellationToken))
				return;

			foreach (var item in SqlTextCatalogGenerator.TransformNode(context.Node, context.SemanticModel, context.CancellationToken))
			{
				if (string.Equals(item.Partition, SqlTextCatalogGenerator.UnknownPartition, System.StringComparison.Ordinal))
					context.ReportDiagnostic(Diagnostic.Create(ConfigurationRule, context.Node.GetLocation(), item.Text));
				else if (SqlTextCatalogGenerator.IsRefused(item))
					context.ReportDiagnostic(Diagnostic.Create(QuotedRule, context.Node.GetLocation(), item.Text));
			}
		}
	}
}
