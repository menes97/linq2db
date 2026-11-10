using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeGenerators
{
	/// <summary>
	/// Resolves the closed set of strings an expression can produce, shared by the catalog generator and its analyzer.
	/// </summary>
	static class SqlTextFlow
	{
		const string AttributeName = "LinqToDB.Remote.SqlTextAttribute";
		const int    MaxDepth      = 8;
		const int    MaxValues     = 64;

		public static bool TryGetKind(ISymbol symbol, out int kind)
		{
			foreach (var attribute in symbol.GetAttributes())
			{
				if (attribute.AttributeClass?.ToDisplayString() == AttributeName && attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value)
				{
					kind = value;
					return true;
				}
			}

			kind = 0;
			return false;
		}

		public static bool TryGetAssignedKind(ISimpleAssignmentOperation assignment, out int kind)
		{
			if (assignment.Target is IPropertyReferenceOperation target && TryGetKind(target.Property, out kind))
				return true;

			kind = 0;
			return false;
		}

		public static IEnumerable<(IArgumentOperation Argument, int Kind)> GetSqlTextArguments(IOperation operation)
		{
			var arguments = operation switch
			{
				IInvocationOperation     invocation => invocation.Arguments,
				IObjectCreationOperation creation   => creation.Arguments,
				_                                   => default,
			};

			if (arguments.IsDefaultOrEmpty)
				yield break;

			foreach (var argument in arguments)
				if (argument.Parameter != null && TryGetKind(argument.Parameter, out var kind))
					yield return (argument, kind);
		}

		/// <summary>Adds every string <paramref name="operation"/> can produce to <paramref name="values"/>.</summary>
		/// <returns><see langword="false"/> when a value cannot be determined at compile time.</returns>
		public static bool TryResolve(IOperation? operation, SemanticModel model, List<string> values, int depth = 0)
		{
			if (operation == null || depth > MaxDepth || values.Count > MaxValues)
				return false;

			if (operation.ConstantValue is { HasValue: true, Value: var constant })
			{
				if (constant is string text)
					values.Add(text);

				return constant is null or string;
			}

			switch (operation)
			{
				case IFieldReferenceOperation { Field: { Name: "Empty", ContainingType.SpecialType: SpecialType.System_String } }:
					values.Add(string.Empty);
					return true;

				case IInvocationOperation { TargetMethod.Name: "ToString", Instance: { } enumInstance } when GetEnumType(enumInstance) is { } enumType:
					values.AddRange(enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name));
					return true;

				case ICoalesceOperation coalesce:
					return TryResolve(coalesce.Value, model, values, depth + 1)
						&& (IsThrow(coalesce.WhenNull) || TryResolve(coalesce.WhenNull, model, values, depth + 1));

				case IInvocationOperation { TargetMethod.Name: "ToLowerInvariant" or "ToUpperInvariant", Instance: { } instance } caseChange:
				{
					var inner = new List<string>();

					if (!TryResolve(instance, model, inner, depth + 1) || inner.Count == 0)
						return false;

					var lower = caseChange.TargetMethod.Name == "ToLowerInvariant";

					values.AddRange(inner.Select(v => lower ? v.ToLowerInvariant() : v.ToUpperInvariant()));
					return true;
				}

				case IInvocationOperation helper when helper.TargetMethod.ReturnType.SpecialType == SpecialType.System_String && !helper.TargetMethod.IsVirtual && !helper.TargetMethod.IsAbstract:
					return TryResolveHelper(helper.TargetMethod, model, values, depth);

				case IConversionOperation conversion:
					return TryResolve(conversion.Operand, model, values, depth + 1);

				case IParenthesizedOperation parenthesized:
					return TryResolve(parenthesized.Operand, model, values, depth + 1);

				case IConditionalOperation conditional:
					return TryResolve(conditional.WhenTrue, model, values, depth + 1)
						&& TryResolve(conditional.WhenFalse, model, values, depth + 1);

				case ISwitchExpressionOperation switchExpression:
					return switchExpression.Arms.All(arm => IsThrow(arm.Value) || TryResolve(arm.Value, model, values, depth + 1));

				case IParameterReferenceOperation parameter:
					return TryGetKind(parameter.Parameter, out _) || TryResolveFromCallers(parameter.Parameter, model, values, depth);

				case IPropertyReferenceOperation property:
					return TryGetPropertyKind(property.Property, out _);

				case ILocalReferenceOperation local:
					return TryResolveSymbol(local.Local, model, values, depth);

				case IFieldReferenceOperation field when field.Field.IsReadOnly:
					return TryResolveSymbol(field.Field, model, values, depth);

				case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary:
					return TryResolveConcat([binary.LeftOperand, binary.RightOperand], model, values, depth);

				case IInterpolatedStringOperation interpolated:
					return TryResolveInterpolated(interpolated, model, values, depth);

				default:
					return false;
			}
		}

		/// <summary>
		/// Resolves a parameter of a private method or local function from every call in the same file. Nothing outside
		/// the file can call such a method, so the callers seen here are all of them.
		/// </summary>
		static bool TryResolveFromCallers(IParameterSymbol parameter, SemanticModel model, List<string> values, int depth)
		{
			if (parameter.ContainingSymbol is not IMethodSymbol method)
				return false;

			if (method.MethodKind != MethodKind.LocalFunction && method.DeclaredAccessibility != Accessibility.Private)
				return false;

			if (method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree != model.SyntaxTree)
				return false;

			var found = false;

			foreach (var invocation in model.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target || !SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, method))
					continue;

				if (model.GetOperation(invocation) is not IInvocationOperation call)
					return false;

				var argument = call.Arguments.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.OriginalDefinition, parameter));

				if (argument == null || !TryResolve(argument.Value, model, values, depth + 1))
					return false;

				found = true;
			}

			return found;
		}

		static ITypeSymbol? GetEnumType(IOperation instance)
		{
			while (instance is IConversionOperation conversion)
				instance = conversion.Operand;

			return instance.Type is { TypeKind: TypeKind.Enum } type ? type : null;
		}

		/// <summary>Finds the <c>[SqlText]</c> kind of a property, including one declared on a property it overrides.</summary>
		public static bool TryGetPropertyKind(IPropertySymbol? property, out int kind)
		{
			for (; property != null; property = property.OverriddenProperty)
				if (TryGetKind(property, out kind))
					return true;

			kind = 0;
			return false;
		}

		static bool TryResolveHelper(IMethodSymbol method, SemanticModel model, List<string> values, int depth)
		{
			var reference = method.DeclaringSyntaxReferences.FirstOrDefault();

			if (reference == null || reference.SyntaxTree != model.SyntaxTree)
				return false;

			var syntax = reference.GetSyntax();

			var bodyExpression = syntax switch
			{
				MethodDeclarationSyntax m        => m.ExpressionBody?.Expression,
				LocalFunctionStatementSyntax l   => l.ExpressionBody?.Expression,
				_                                => null,
			};

			if (bodyExpression != null)
				return TryResolve(model.GetOperation(bodyExpression), model, values, depth + 1);

			var returns = syntax.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();

			if (returns.Count == 0)
				return false;

			foreach (var statement in returns)
				if (statement.Expression == null || !TryResolve(model.GetOperation(statement.Expression), model, values, depth + 1))
					return false;

			return true;
		}

		static bool IsThrow(IOperation operation)
		{
			return operation is IThrowOperation or IConversionOperation { Operand: IThrowOperation };
		}

		static bool TryResolveSymbol(ISymbol symbol, SemanticModel model, List<string> values, int depth)
		{
			var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();

			if (reference?.GetSyntax() is not VariableDeclaratorSyntax declarator || reference.SyntaxTree != model.SyntaxTree)
				return false;

			var resolved = false;

			if (declarator.Initializer != null)
			{
				if (!TryResolve(model.GetOperation(declarator.Initializer.Value), model, values, depth + 1))
					return false;

				resolved = true;
			}

			if (symbol is ILocalSymbol)
			{
				var body = declarator.Ancestors().FirstOrDefault(static a => a is BlockSyntax or ArrowExpressionClauseSyntax);

				foreach (var assignment in body?.DescendantNodes().OfType<AssignmentExpressionSyntax>() ?? [])
				{
					if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, symbol))
						continue;

					if (!TryResolve(model.GetOperation(assignment.Right), model, values, depth + 1))
						return false;

					resolved = true;
				}
			}

			return resolved;
		}

		static bool TryResolveConcat(IReadOnlyList<IOperation> parts, SemanticModel model, List<string> values, int depth)
		{
			var current = new List<string> { string.Empty };

			foreach (var part in parts)
			{
				var options = new List<string>();

				if (!TryResolve(part, model, options, depth + 1))
					return false;

				var next = new List<string>(current.Count * options.Count);

				foreach (var left in current)
					foreach (var right in options)
						next.Add(left + right);

				if (next.Count > MaxValues)
					return false;

				current = next;
			}

			values.AddRange(current);
			return true;
		}

		static bool TryResolveInterpolated(IInterpolatedStringOperation interpolated, SemanticModel model, List<string> values, int depth)
		{
			var current = new List<string> { string.Empty };

			foreach (var part in interpolated.Parts)
			{
				var options = new List<string>();

				switch (part)
				{
					case IInterpolatedStringTextOperation text:
						options.Add((string)text.Text.ConstantValue.Value!);
						break;

					case IInterpolationOperation { Alignment: null, FormatString: null } interpolation:
						if (!TryResolve(interpolation.Expression, model, options, depth + 1))
							return false;
						break;

					default:
						return false;
				}

				var next = new List<string>(current.Count * options.Count);

				foreach (var left in current)
					foreach (var right in options)
						next.Add(left + right);

				if (next.Count > MaxValues)
					return false;

				current = next;
			}

			values.AddRange(current);
			return true;
		}
	}
}
