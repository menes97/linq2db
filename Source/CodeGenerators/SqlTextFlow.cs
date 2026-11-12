using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
				if (string.Equals(attribute.AttributeClass?.ToDisplayString(), AttributeName, StringComparison.Ordinal) && attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value)
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

					var lower = string.Equals(caseChange.TargetMethod.Name, "ToLowerInvariant", StringComparison.Ordinal);

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

		static bool TryResolveSymbol(ISymbol symbol, SemanticModel model, List<string> output, int depth)
		{
			var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();

			if (reference == null || reference.SyntaxTree != model.SyntaxTree)
				return false;

			var values   = new List<string>();
			var resolved = false;
			var syntax   = reference.GetSyntax();

			switch (syntax)
			{
				case VariableDeclaratorSyntax { Initializer: { } initializer }:
					if (!TryResolve(model.GetOperation(initializer.Value), model, values, depth + 1))
						return false;

					resolved = true;
					break;

				case VariableDeclaratorSyntax:
					break;

				// var (a, b) = ...
				case SingleVariableDesignationSyntax { Parent: ParenthesizedVariableDesignationSyntax tuple } designation
					when tuple.Parent is DeclarationExpressionSyntax { Parent: AssignmentExpressionSyntax assignment } declaration && assignment.Left == declaration:
					if (!TryResolveTupleElement(model.GetOperation(assignment.Right), tuple.Variables.IndexOf(designation), model, values, depth + 1))
						return false;

					resolved = true;
					break;

				// dictionary.TryGetValue(key, out var a)
				case SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } } }:
					if (!TryResolveDictionaryValues(model.GetOperation(invocation), model, values, depth + 1))
						return false;

					resolved = true;
					break;

				default:
					return false;
			}

			if (symbol is ILocalSymbol)
			{
				var body = syntax.Ancestors().FirstOrDefault(static a => a is BlockSyntax or ArrowExpressionClauseSyntax);

				foreach (var assignment in body?.DescendantNodes().OfType<AssignmentExpressionSyntax>() ?? [])
				{
					if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, symbol))
						continue;

					var right = new List<string>();

					if (!TryResolve(model.GetOperation(assignment.Right), model, right, depth + 1))
						return false;

					switch (assignment.Kind())
					{
						case SyntaxKind.SimpleAssignmentExpression:
						case SyntaxKind.CoalesceAssignmentExpression:
							values.AddRange(right);
							break;

						// Appends cross every value seen so far, in source order: a superset of the texts the code builds,
						// all made of the same literals. A loop would append without bound.
						case SyntaxKind.AddAssignmentExpression when resolved && !assignment.Ancestors().TakeWhile(a => a != body).Any(IsLoop):
						{
							var appended = values.SelectMany(left => right.Select(r => left + r)).ToList();

							if (appended.Count > MaxValues)
								return false;

							values.Clear();
							values.AddRange(appended);
							break;
						}

						default:
							return false;
					}

					resolved = true;
				}
			}

			if (resolved)
				output.AddRange(values);

			return resolved;
		}

		static bool IsLoop(SyntaxNode node)
		{
			return node is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax;
		}

		static bool TryResolveTupleElement(IOperation? operation, int index, SemanticModel model, List<string> values, int depth)
		{
			if (operation == null || index < 0 || depth > MaxDepth)
				return false;

			return operation switch
			{
				ITupleOperation tuple                     => index < tuple.Elements.Length && TryResolve(tuple.Elements[index], model, values, depth + 1),
				ISwitchExpressionOperation switchExpr     => switchExpr.Arms.All(arm => IsThrow(arm.Value) || TryResolveTupleElement(arm.Value, index, model, values, depth + 1)),
				IConditionalOperation conditional         => TryResolveTupleElement(conditional.WhenTrue, index, model, values, depth + 1) && TryResolveTupleElement(conditional.WhenFalse, index, model, values, depth + 1),
				IConversionOperation conversion           => TryResolveTupleElement(conversion.Operand, index, model, values, depth + 1),
				IParenthesizedOperation parenthesized     => TryResolveTupleElement(parenthesized.Operand, index, model, values, depth + 1),
				_                                         => false,
			};
		}

		/// <summary>
		/// Resolves the value written by <c>TryGetValue(key, out var value)</c> on a read-only <c>IReadOnlyDictionary</c>
		/// field: every value of the field's collection initializer.
		/// </summary>
		static bool TryResolveDictionaryValues(IOperation? operation, SemanticModel model, List<string> values, int depth)
		{
			if (operation is not IInvocationOperation { TargetMethod.Name: "TryGetValue", Instance: { } instance })
				return false;

			while (instance is IConversionOperation conversion)
				instance = conversion.Operand;

			if (instance is not IFieldReferenceOperation { Field: { IsReadOnly: true } field }
				|| field.Type is not INamedTypeSymbol { OriginalDefinition: { } fieldType }
				|| !string.Equals(fieldType.ToDisplayString(), "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>", StringComparison.Ordinal))
				return false;

			if (field.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference
				|| reference.SyntaxTree != model.SyntaxTree
				|| reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer: { } initializer })
				return false;

			var creation = model.GetOperation(initializer.Value);

			while (creation is IConversionOperation conversion)
				creation = conversion.Operand;

			if (creation is not IObjectCreationOperation { Initializer: { } collection } || collection.Initializers.IsEmpty)
				return false;

			foreach (var element in collection.Initializers)
			{
				var value = element switch
				{
					IInvocationOperation { Arguments.Length: 2 } add           => add.Arguments[1].Value,
					ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } } set => set.Value,
					_                                                         => null,
				};

				if (value == null || !TryResolve(value, model, values, depth + 1))
					return false;
			}

			return true;
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
