using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// The public linq2db call sites that send SQL text, with the kind the remote validator checks and the shape the text has
	/// on the wire. Every gap here is a false rejection at run time, never an admitted text.
	/// </summary>
	static class SqlTextCallSites
	{
		enum Shape
		{
			/// <summary>The argument text as written.</summary>
			Verbatim,
			/// <summary>A <c>FormattableString</c> or <c>RawSqlString</c>: the composite format, holes numbered in order.</summary>
			Format,
			/// <summary><c>ITable.With(hint)</c> inside an expression tree: <c>{0} {1} WITH (hint)</c> (WithTableExpressionBuilder).</summary>
			WithHint,
		}

		readonly record struct Row(string Type, string? Method, string Parameter, string Kind, Shape Shape);

		const string QueryExtensionAttribute = "LinqToDB.Sql.QueryExtensionAttribute";

		// Validator text keys of extension builder arguments (RemoteQueryValidator._textKeys); identifier keys are not catalogued.
		static readonly HashSet<string> _hintTextKeys = new(StringComparer.Ordinal) { "hint", "hint2", "hintFormat", "values", "expression" };

		static readonly Row[] _rows =
		[
			new("LinqToDB.DataExtensions",        "FromSql",             "sql",             "RawSqlTable",     Shape.Format),
			new("LinqToDB.DataExtensions",        "FromSqlScalar",       "sql",             "RawSqlTable",     Shape.Format),
			new("LinqToDB.Sql",                   "Expr",                "sql",             "Expression",      Shape.Format),
			new("LinqToDB.LinqExtensions",        "With",                "hint",            "TableExpression", Shape.WithHint),
			new("LinqToDB.LinqExtensions",        "WithTableExpression", "expression",      "TableExpression", Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "TableName",           "name",            "TableName",       Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "SchemaName",          "name",            "TableName",       Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "DatabaseName",        "name",            "TableName",       Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "ServerName",          "name",            "TableName",       Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "Merge",               "hint",            "MergeHint",       Shape.Verbatim),
			new("LinqToDB.LinqExtensions",        "MergeInto",           "hint",            "MergeHint",       Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "tableName",       "TableName",       Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "schemaName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "databaseName",    "TableName",       Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "serverName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "statementHeader", "StatementHeader", Shape.Verbatim),
			new("LinqToDB.DataExtensions",        null,                  "statementFooter", "StatementFooter", Shape.Verbatim),
			new("LinqToDB.DataOptionsExtensions", null,                  "tableName",       "TableName",       Shape.Verbatim),
			new("LinqToDB.DataOptionsExtensions", null,                  "schemaName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.DataOptionsExtensions", null,                  "databaseName",    "TableName",       Shape.Verbatim),
			new("LinqToDB.DataOptionsExtensions", null,                  "serverName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.TempTable",             null,                  "tableName",       "TableName",       Shape.Verbatim),
			new("LinqToDB.TempTable",             null,                  "schemaName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.TempTable",             null,                  "databaseName",    "TableName",       Shape.Verbatim),
			new("LinqToDB.TempTable",             null,                  "serverName",      "TableName",       Shape.Verbatim),
			new("LinqToDB.MergeDefinition",       ".ctor",               "hint",            "MergeHint",       Shape.Verbatim),
			new("LinqToDB.DbDataType",            ".ctor",               "dbType",          "DbType",          Shape.Verbatim),
			new("LinqToDB.DbDataType",            "WithDbType",          "dbName",          "DbType",          Shape.Verbatim),
			new("LinqToDB.Mapping.PropertyMappingBuilder", "HasDbType",  "dbType",          "DbType",          Shape.Verbatim),
		];

		// Option records: properties set by assignment, object initializer, init or with, and the same-named constructor arguments.
		static readonly Dictionary<string, Dictionary<string, string>> _properties = new(StringComparer.Ordinal)
		{
			["LinqToDB.CreateTableOptions"]     = TableNames(withStatement: true),
			["LinqToDB.CreateTempTableOptions"] = TableNames(withStatement: true),
			["LinqToDB.Data.BulkCopyOptions"]   = TableNames(withStatement: false),
		};

		static readonly HashSet<string> _hintLists = new(StringComparer.Ordinal) { "QueryHints", "NextQueryHints" };

		static Dictionary<string, string> TableNames(bool withStatement)
		{
			var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["TableName"]    = "TableName",
				["SchemaName"]   = "TableName",
				["DatabaseName"] = "TableName",
				["ServerName"]   = "TableName",
			};

			if (withStatement)
			{
				names["StatementHeader"] = "StatementHeader";
				names["StatementFooter"] = "StatementFooter";
			}

			return names;
		}

		static readonly string[] _attributeTypeNames =
		[
			"ExpressionAttribute", "FunctionAttribute", "PropertyAttribute", "ExtensionAttribute", "TableFunctionAttribute", "TableExpressionAttribute",
		];

		/// <summary>
		/// The identifiers a member has to spell to contain a site: the rows, option properties, hint lists and attribute
		/// types above, every linq2db method or property marked <c>[Sql.QueryExtension]</c> or <c>[SqlText]</c>, and source
		/// methods declaring either attribute. Read once per compilation from declarations only.
		/// </summary>
		internal static HashSet<string> GetSiteNames(Compilation compilation, System.Threading.CancellationToken cancellationToken)
		{
			var names = new HashSet<string>(_hintLists, StringComparer.Ordinal);

			names.UnionWith(_attributeTypeNames);

			foreach (var pair in _properties)
			{
				names.Add(pair.Key.Substring(pair.Key.LastIndexOf('.') + 1));
				names.UnionWith(pair.Value.Keys);
			}

			foreach (var row in _rows)
			{
				if (row.Method is { } method)
				{
					names.Add(string.Equals(method, ".ctor", StringComparison.Ordinal) ? row.Type.Substring(row.Type.LastIndexOf('.') + 1) : method);
					continue;
				}

				// a type-wide row: every method of the type taking the parameter
				foreach (var type in GetTypes(compilation, row.Type))
					foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
						if (member.Parameters.Any(p => string.Equals(p.Name, row.Parameter, StringComparison.Ordinal)))
							names.Add(member.MethodKind == MethodKind.Constructor ? type.Name : member.Name);
			}

			if (compilation.GetTypeByMetadataName("LinqToDB.Sql") is { } sql)
				AddAnnotatedMembers(sql.ContainingAssembly.GlobalNamespace, names, cancellationToken);

			foreach (var tree in compilation.SyntaxTrees)
			{
				foreach (var attribute in tree.GetRoot(cancellationToken).DescendantNodes().OfType<AttributeSyntax>())
				{
					var name = attribute.Name is QualifiedNameSyntax qualified ? qualified.Right.Identifier.ValueText : attribute.Name.ToString();

					if (name is not ("QueryExtension" or "QueryExtensionAttribute" or "SqlText" or "SqlTextAttribute"))
						continue;

					switch (attribute.FirstAncestorOrSelf<MemberDeclarationSyntax>())
					{
						case MethodDeclarationSyntax m      : names.Add(m.Identifier.ValueText); break;
						case PropertyDeclarationSyntax p    : names.Add(p.Identifier.ValueText); break;
						case ConstructorDeclarationSyntax c : names.Add(c.Identifier.ValueText); break;
					}
				}
			}

			return names;
		}

		static IEnumerable<INamedTypeSymbol> GetTypes(Compilation compilation, string typeName)
		{
			foreach (var type in compilation.GetTypesByMetadataName(typeName))
				yield return type;

			// a generic row type is keyed without its type arguments: TempTable<T> is "LinqToDB.TempTable"
			foreach (var type in compilation.GetTypesByMetadataName(typeName + "`1"))
				yield return type;
		}

		static void AddAnnotatedMembers(INamespaceSymbol ns, HashSet<string> names, System.Threading.CancellationToken cancellationToken)
		{
			foreach (var member in ns.GetMembers())
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (member is INamespaceSymbol child)
					AddAnnotatedMembers(child, names, cancellationToken);
				else if (member is INamedTypeSymbol type)
					AddAnnotatedMembers(type, names);
			}
		}

		static void AddAnnotatedMembers(INamedTypeSymbol type, HashSet<string> names)
		{
			if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
				return;

			foreach (var member in type.GetMembers())
			{
				switch (member)
				{
					case INamedTypeSymbol nested:
						AddAnnotatedMembers(nested, names);
						break;

					case IMethodSymbol method when method.GetAttributes().Any(static a => string.Equals(a.AttributeClass?.ToDisplayString(), QueryExtensionAttribute, StringComparison.Ordinal))
						|| method.Parameters.Any(static p => CodeGenerators.SqlTextFlow.TryGetKind(p, out _)):
						names.Add(method.MethodKind == MethodKind.Constructor ? type.Name : method.Name);
						break;

					case IPropertySymbol property when CodeGenerators.SqlTextFlow.TryGetKind(property, out _):
						names.Add(property.Name);
						break;
				}
			}
		}

		/// <summary>Adds the text one operation sends. Child operations are visited by the caller.</summary>
		public static void Collect(IOperation operation, SqlTextCollector collector)
		{
			switch (operation)
			{
				case IInvocationOperation invocation:
					CollectAnnotated(invocation, collector);
					CollectArguments(invocation.TargetMethod, invocation.Arguments, collector);
					CollectHintList(invocation, collector);
					break;

				case IObjectCreationOperation creation:
					CollectAnnotated(creation, collector);

					if (creation.Constructor is { } constructor)
					{
						CollectArguments(constructor, creation.Arguments, collector);

						var typeName = TypeName(constructor.ContainingType);

						foreach (var argument in creation.Arguments)
							if (argument.Parameter != null && !IsDefault(argument) && ClassifyProperty(typeName, argument.Parameter.Name) is { } kind)
								collector.AddResolved(kind, argument.Value, argument.Syntax.GetLocation());
					}

					break;

				case ISimpleAssignmentOperation assignment:
					if (CodeGenerators.SqlTextFlow.TryGetAssignedKind(assignment, out var assigned) && collector.Kinds.FromValue(assigned) is { } assignedKind)
						collector.AddResolved(assignedKind, assignment.Value, assignment.Syntax.GetLocation());

					if (assignment.Target is IPropertyReferenceOperation { Property: var property }
						&& ClassifyProperty(TypeName(property.ContainingType), property.Name) is { } propertyKind)
					{
						collector.AddResolved(propertyKind, assignment.Value, assignment.Syntax.GetLocation());
					}

					break;
			}
		}

		// Arguments to linq2db parameters marked [SqlText], as in user translators and extension builders.
		static void CollectAnnotated(IOperation operation, SqlTextCollector collector)
		{
			foreach (var (argument, value) in CodeGenerators.SqlTextFlow.GetSqlTextArguments(operation))
				if (collector.Kinds.FromValue(value) is { } kind)
					collector.AddResolved(kind, argument.Value, argument.Syntax.GetLocation());
		}

		/// <summary>
		/// The kinds a parameter of a public linq2db method is catalogued as, with their wire shape. Shared with the
		/// public-surface guard test, so the rules have one source.
		/// </summary>
		internal static IEnumerable<(string Kind, string Shape)> Classify(string typeName, string methodName, string parameterName, bool isHintMethod, bool isTextType)
		{
			if (isHintMethod && isTextType && _hintTextKeys.Contains(parameterName))
				yield return ("ExtensionArgument", nameof(Shape.Verbatim));

			foreach (var row in GetRows(typeName, methodName, parameterName))
				yield return (row.Kind, row.Shape.ToString());

			if (string.Equals(methodName, ".ctor", StringComparison.Ordinal) && ClassifyProperty(typeName, parameterName) is { } kind)
				yield return (kind, nameof(Shape.Verbatim));
		}

		/// <summary>The kind a public property of a linq2db option record is catalogued as.</summary>
		internal static string? ClassifyProperty(string typeName, string propertyName)
		{
			return _properties.TryGetValue(typeName, out var names) && names.TryGetValue(propertyName, out var kind) ? kind : null;
		}

		static IEnumerable<Row> GetRows(string typeName, string methodName, string parameterName)
		{
			foreach (var row in _rows)
			{
				if (string.Equals(row.Type, typeName, StringComparison.Ordinal)
					&& (row.Method == null || string.Equals(row.Method, methodName, StringComparison.Ordinal))
					&& string.Equals(row.Parameter, parameterName, StringComparison.Ordinal))
				{
					yield return row;
				}
			}
		}

		static void CollectArguments(IMethodSymbol method, IEnumerable<IArgumentOperation> arguments, SqlTextCollector collector)
		{
			var definition = method.ReducedFrom ?? method;
			var typeName   = TypeName(definition.ContainingType);
			var isHint     = definition.GetAttributes().Any(static a => string.Equals(a.AttributeClass?.ToDisplayString(), QueryExtensionAttribute, StringComparison.Ordinal));

			foreach (var argument in arguments)
			{
				if (argument.Parameter is not { } parameter || IsDefault(argument))
					continue;

				if (isHint && _hintTextKeys.Contains(parameter.Name) && IsTextType(parameter.Type))
					AddVerbatim("ExtensionArgument", argument, collector);

				foreach (var row in GetRows(typeName, definition.Name, parameter.Name))
				{
					switch (row.Shape)
					{
						case Shape.Verbatim:
							AddVerbatim(row.Kind, argument, collector);
							break;

						case Shape.Format:
						{
							var values = new List<string>();

							if (TryResolveFormat(argument.Value, collector.Model, values))
								collector.AddValues(row.Kind, values, argument.Syntax.GetLocation());
							else
								collector.ReportNotConstant(argument.Value, argument.Syntax.GetLocation());

							break;
						}

						case Shape.WithHint:
						{
							var values = new List<string>();

							if (CodeGenerators.SqlTextFlow.TryResolve(argument.Value, collector.Model, values))
								collector.AddValues(row.Kind, values.Select(static v => "{0} {1} WITH (" + v + ")"), argument.Syntax.GetLocation());
							else
								collector.ReportNotConstant(argument.Value, argument.Syntax.GetLocation());

							break;
						}
					}
				}
			}
		}

		static readonly SymbolDisplayFormat _typeNameFormat = new(
			typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
			genericsOptions:        SymbolDisplayGenericsOptions.None);

		// Namespace-qualified, nested types dot-separated, without type arguments: TempTable<T> is "LinqToDB.TempTable".
		static string TypeName(INamedTypeSymbol type)
		{
			return type.OriginalDefinition.ToDisplayString(_typeNameFormat);
		}

		static bool IsDefault(IArgumentOperation argument)
		{
			return argument.ArgumentKind == ArgumentKind.DefaultValue;
		}

		static bool IsTextType(ITypeSymbol type)
		{
			return type.SpecialType == SpecialType.System_String
				|| type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String };
		}

		static void AddVerbatim(string kind, IArgumentOperation argument, SqlTextCollector collector)
		{
			if (argument.Value.Type is IArrayTypeSymbol)
			{
				if (argument.Value is IArrayCreationOperation { Initializer: { } initializer })
					foreach (var element in initializer.ElementValues)
						collector.AddResolved(kind, element, element.Syntax.GetLocation());
				else
					collector.ReportNotConstant(argument.Value, argument.Syntax.GetLocation());

				return;
			}

			collector.AddResolved(kind, argument.Value, argument.Syntax.GetLocation());
		}

		// db.QueryHints.Add("..."), on the interface or an implementing property, directly or through a local initialized from it.
		static void CollectHintList(IInvocationOperation invocation, SqlTextCollector collector)
		{
			if (invocation.TargetMethod.Name is not ("Add" or "AddRange" or "Insert") || invocation.Instance is not { } instance)
				return;

			var isAlias = false;

			instance = Unwrap(instance);

			if (instance is ILocalReferenceOperation local)
			{
				if (!TryGetInitializer(local.Local, collector.Model, out var initializer))
					return;

				instance = Unwrap(initializer);
				isAlias  = true;
			}

			if (instance is not IPropertyReferenceOperation { Property: var property } || !IsHintList(property))
				return;

			if (isAlias && HasReassignment(((ILocalReferenceOperation)Unwrap(invocation.Instance!)).Local, collector.Model))
			{
				collector.ReportNotConstant(invocation.Instance!, invocation.Syntax.GetLocation());
				return;
			}

			var value = invocation.TargetMethod.Name switch
			{
				"Insert" => invocation.Arguments.Length > 1 ? invocation.Arguments[1].Value : null,
				_        => invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null,
			};

			if (value == null)
				return;

			if (string.Equals(invocation.TargetMethod.Name, "AddRange", StringComparison.Ordinal))
			{
				if (Unwrap(value) is IArrayCreationOperation { Initializer: { } initializer })
					foreach (var element in initializer.ElementValues)
						collector.AddResolved("QueryHint", element, element.Syntax.GetLocation());
				else
					collector.ReportNotConstant(value, value.Syntax.GetLocation());

				return;
			}

			collector.AddResolved("QueryHint", value, value.Syntax.GetLocation());
		}

		static bool IsHintList(IPropertySymbol property)
		{
			if (!_hintLists.Contains(property.Name))
				return false;

			if (IsDataContext(property.ContainingType))
				return true;

			foreach (var implemented in property.ExplicitInterfaceImplementations)
				if (IsDataContext(implemented.ContainingType))
					return true;

			foreach (var iface in property.ContainingType.AllInterfaces)
			{
				if (!IsDataContext(iface))
					continue;

				foreach (var member in iface.GetMembers(property.Name))
					if (SymbolEqualityComparer.Default.Equals(property.ContainingType.FindImplementationForInterfaceMember(member), property))
						return true;
			}

			return false;
		}

		static bool IsDataContext(INamedTypeSymbol type)
		{
			return string.Equals(type.ToDisplayString(), "LinqToDB.IDataContext", StringComparison.Ordinal);
		}

		static IOperation Unwrap(IOperation operation)
		{
			while (operation is IConversionOperation conversion)
				operation = conversion.Operand;

			return operation;
		}

		static bool TryGetInitializer(ILocalSymbol local, SemanticModel model, out IOperation initializer)
		{
			initializer = null!;

			if (local.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference
				|| reference.SyntaxTree != model.SyntaxTree
				|| reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var value }
				|| model.GetOperation(value) is not { } operation)
			{
				return false;
			}

			initializer = operation;
			return true;
		}

		static bool HasReassignment(ILocalSymbol local, SemanticModel model)
		{
			var reference = local.DeclaringSyntaxReferences.FirstOrDefault();
			var body      = reference?.GetSyntax().Ancestors().FirstOrDefault(static a => a is BlockSyntax or ArrowExpressionClauseSyntax);

			return body?.DescendantNodes().OfType<AssignmentExpressionSyntax>()
				.Any(a => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, local)) == true;
		}

		/// <summary>
		/// Rebuilds the composite format the runtime sends for a <c>FormattableString</c> (<c>FormattableString.Format</c>) or a
		/// <c>RawSqlString</c> (its text as written).
		/// </summary>
		static bool TryResolveFormat(IOperation value, SemanticModel model, List<string> values)
		{
			value = Unwrap(value);

			if (value is IInterpolatedStringOperation interpolated)
			{
				var sb    = new StringBuilder();
				var index = 0;

				foreach (var part in interpolated.Parts)
				{
					switch (part)
					{
						case IInterpolatedStringTextOperation text when text.Text.ConstantValue is { HasValue: true, Value: string literal }:
							sb.Append(literal.Replace("{", "{{").Replace("}", "}}"));
							break;

						case IInterpolationOperation hole:
							sb.Append('{').Append(index++);

							if (hole.Alignment != null)
							{
								if (hole.Alignment.ConstantValue is not { HasValue: true, Value: int alignment })
									return false;

								sb.Append(',').Append(alignment.ToString(System.Globalization.CultureInfo.InvariantCulture));
							}

							if (hole.FormatString != null)
							{
								if (hole.FormatString.ConstantValue is not { HasValue: true, Value: string format })
									return false;

								sb.Append(':').Append(format);
							}

							sb.Append('}');
							break;

						default:
							return false;
					}
				}

				values.Add(sb.ToString());
				return true;
			}

			return value.Type?.SpecialType == SpecialType.System_String && CodeGenerators.SqlTextFlow.TryResolve(value, model, values);
		}
	}
}
