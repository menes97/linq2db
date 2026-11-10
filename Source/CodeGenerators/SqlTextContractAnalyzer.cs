using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGenerators
{
	/// <summary>
	/// Reports SQL text the catalog generator cannot determine. An uncatalogued text fails at runtime, and only for
	/// untrusted remote clients, so without this rule a new translator would pass every test and break in production.
	/// </summary>
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class SqlTextContractAnalyzer : DiagnosticAnalyzer
	{
		static readonly DiagnosticDescriptor Rule = new(
			id:                 "LINQ2DB0007",
			title:              "SQL text must be known at compile time",
			messageFormat:      "The value passed to '{0}' cannot be catalogued: use a constant, a [SqlText] parameter, or a local or field assigned only such values",
			category:           "Usage",
			defaultSeverity:    DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description:        "Text passed to a [SqlText] position is accepted from untrusted LinqService clients only when it is in the generated catalog.");

		/// <inheritdoc/>
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

		/// <inheritdoc/>
		public override void Initialize(AnalysisContext context)
		{
			context.EnableConcurrentExecution();
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

			context.RegisterOperationAction(AnalyzeCall, OperationKind.Invocation, OperationKind.ObjectCreation);
			context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
		}

		static void AnalyzeCall(OperationAnalysisContext context)
		{
			foreach (var (argument, _) in SqlTextFlow.GetSqlTextArguments(context.Operation))
				Report(context, argument.Value, argument.Parameter!.Name);
		}

		static void AnalyzeAssignment(OperationAnalysisContext context)
		{
			var assignment = (ISimpleAssignmentOperation)context.Operation;

			if (SqlTextFlow.TryGetAssignedKind(assignment, out _))
				Report(context, assignment.Value, ((IPropertyReferenceOperation)assignment.Target).Property.Name);
		}

		static void Report(OperationAnalysisContext context, IOperation value, string target)
		{
			if (value.SemanticModel == null)
				return;

			if (!SqlTextFlow.TryResolve(value, value.SemanticModel, new List<string>()))
				context.ReportDiagnostic(Diagnostic.Create(Rule, value.Syntax.GetLocation(), target));
		}
	}
}
