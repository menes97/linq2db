using System;
using System.Collections.Generic;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace LinqToDB.Analyzers
{
	/// <summary>
	/// The SQL text of linq2db's mapping attributes, declared or created with <c>new</c> for fluent mapping, with the kind the
	/// remote validator checks it as. Shared by the library and the user catalog generators, so both read attributes alike:
	/// it returns kind names and the attribute's provider configuration, and each generator maps them to its own entries.
	/// </summary>
	static class SqlTextAttributeSources
	{
		const string SqlTypeName = "LinqToDB.Sql";

		/// <summary>The texts <paramref name="attribute"/>, applied to <paramref name="owner"/>, renders.</summary>
		public static IEnumerable<(string Kind, string Text, string? Configuration)> GetTexts(AttributeData attribute, ISymbol owner)
		{
			var type = attribute.AttributeClass;

			if (type == null || attribute.AttributeConstructor is not { } constructor)
				yield break;

			var configuration = GetConfiguration(attribute, constructor);

			if (IsDbTypeAttribute(type))
			{
				foreach (var dbType in GetArguments(attribute, constructor, "dbType", "DbType"))
					yield return ("DbType", dbType, configuration);

				yield break;
			}

			if (IsSqlAttribute(type, "QueryExtensionAttribute"))
			{
				for (var i = 0; i < constructor.Parameters.Length && i < attribute.ConstructorArguments.Length; i++)
				{
					if (!constructor.Parameters[i].Name.StartsWith("extensionArgument", StringComparison.Ordinal))
						continue;

					var argument = attribute.ConstructorArguments[i];

					if (argument.Kind == TypedConstantKind.Array)
					{
						foreach (var element in argument.Values)
							if (AsString(element) is { } text)
								yield return ("ExtensionArgument", text, configuration);
					}
					else if (AsString(argument) is { } text)
					{
						yield return ("ExtensionArgument", text, configuration);
					}
				}

				yield break;
			}

			if (GetKind(type, out var isBase, out var textMember) is not { } kind)
				yield break;

			string? value = null;

			foreach (var text in GetArguments(attribute, constructor, textMember, textMember))
				value = text;

			// a derived attribute forwards its text to the base constructor under a parameter of its own naming (SqlType's
			// SqlExpression(string name)); a table expression's Name is a table name, so it is not read as the text
			if (value == null && !isBase && kind is "Expression" or "Function")
				foreach (var text in GetArguments(attribute, constructor, string.Equals(textMember, "Name", StringComparison.Ordinal) ? "expression" : "name", ""))
					value = text;

			// the runtime falls back to the member name for the base attributes only (ExpressionAttribute.PrepareParameterValues,
			// TableFunctionAttribute.SetTable); a derived attribute builds its own text
			value ??= isBase ? owner.Name : null;

			if (!string.IsNullOrEmpty(value))
				yield return (kind, value!, configuration);

			// a table's name parts are rendered as written and checked part by part as TableName text: a table function's
			// through its object name, a table expression's through {0}, where Name is the table name rather than the text
			if (kind is "TableName" or "TableExpression")
			{
				foreach (var named in attribute.NamedArguments)
				{
					if (AsString(named.Value) is not { Length: > 0 } part)
						continue;

					if (named.Key is "Schema" or "Database" or "Server" or "Package"
						|| (string.Equals(named.Key, "Name", StringComparison.Ordinal) && string.Equals(kind, "TableExpression", StringComparison.Ordinal)))
					{
						yield return ("TableName", part, configuration);
					}
				}
			}
		}

		/// <summary>The texts <c>new Sql.*Attribute(...)</c> passes, as fluent mapping does, as operations still to resolve.</summary>
		public static IEnumerable<(string Kind, IOperation Value)> GetTextValues(IObjectCreationOperation creation)
		{
			if (creation.Constructor is not { } constructor || GetKind(constructor.ContainingType, out _, out var textMember) is not { } kind)
				yield break;

			foreach (var argument in creation.Arguments)
				if (argument.Parameter is { } parameter && IsTextMember(parameter.Name, textMember))
					yield return (kind, argument.Value);

			if (creation.Initializer == null)
				yield break;

			foreach (var initializer in creation.Initializer.Initializers)
			{
				if (initializer is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: var name } } assignment)
					continue;

				if (IsTextMember(name, textMember))
					yield return (kind, assignment.Value);
				else if (kind is "TableName" or "TableExpression" && name is "Schema" or "Database" or "Server" or "Package")
					yield return ("TableName", assignment.Value);
			}
		}

		// The provider configuration the attribute is restricted to: a 'configuration' or 'sqlProvider' constructor
		// parameter, or the Configuration property. The library generator partitions by it; a user catalog does not.
		static string? GetConfiguration(AttributeData attribute, IMethodSymbol constructor)
		{
			string? configuration = null;

			for (var i = 0; i < constructor.Parameters.Length && i < attribute.ConstructorArguments.Length; i++)
				if (constructor.Parameters[i].Name is "configuration" or "sqlProvider")
					configuration = AsString(attribute.ConstructorArguments[i]);

			foreach (var named in attribute.NamedArguments)
				if (string.Equals(named.Key, "Configuration", StringComparison.Ordinal))
					configuration = AsString(named.Value);

			return string.IsNullOrEmpty(configuration) ? null : configuration;
		}

		// Constructor arguments for parameter, then named arguments for property, in source order.
		static IEnumerable<string> GetArguments(AttributeData attribute, IMethodSymbol constructor, string parameter, string property)
		{
			for (var i = 0; i < constructor.Parameters.Length && i < attribute.ConstructorArguments.Length; i++)
				if (IsTextMember(constructor.Parameters[i].Name, parameter) && AsString(attribute.ConstructorArguments[i]) is { Length: > 0 } text)
					yield return text;

			foreach (var named in attribute.NamedArguments)
				if (IsTextMember(named.Key, property) && AsString(named.Value) is { Length: > 0 } text)
					yield return text;
		}

		// TypedConstant.Value throws for an array argument (ArgIndices = new[] { ... }), so every read goes through here.
		static string? AsString(TypedConstant constant)
		{
			return constant.Kind == TypedConstantKind.Array ? null : constant.Value as string;
		}

		static bool IsTextMember(string name, string textMember)
		{
			return string.Equals(name, textMember, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// The kind of a <c>Sql.*Attribute</c>, whether <paramref name="type"/> is that base attribute itself (which gets the
		/// member-name fallback) and the member carrying its text: Name for the function families, Expression otherwise. A
		/// TableExpressionAttribute derives from TableFunctionAttribute, and its Name is a table name, not its text. A table
		/// function's name is rendered as its object name, checked as TableName text; its Expression may only repeat it.
		/// </summary>
		static string? GetKind(INamedTypeSymbol? type, out bool isBase, out string textMember)
		{
			isBase     = true;
			textMember = "Expression";

			for (; type != null; type = type.BaseType)
			{
				if (type.ContainingType is not { } container || !string.Equals(container.ToDisplayString(), SqlTypeName, StringComparison.Ordinal))
				{
					isBase = false;
					continue;
				}

				switch (type.Name)
				{
					case "FunctionAttribute"        :                 textMember = "Name"; return "Function";
					case "TableFunctionAttribute"   :                 textMember = "Name"; return "TableName";
					case "TableExpressionAttribute" : isBase = false;                      return "TableExpression";
					case "PropertyAttribute"        :                 textMember = "Name"; return "Expression";
					case "ExtensionAttribute"       : isBase = false;                      return "Expression";
					case "ExpressionAttribute"      :                                      return "Expression";
				}

				isBase = false;
			}

			return null;
		}

		// A mapped column's DbType also matches the server's own ColumnDescriptor; the catalog entry covers a mapping the
		// server does not hold, such as one in a client-only assembly read through SqlTextAssemblies.
		static bool IsDbTypeAttribute(INamedTypeSymbol type)
		{
			for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
				if (t.Name is "ColumnAttribute" or "DataTypeAttribute" && string.Equals(t.ContainingNamespace?.ToDisplayString(), "LinqToDB.Mapping", StringComparison.Ordinal))
					return true;

			return false;
		}

		static bool IsSqlAttribute(INamedTypeSymbol type, string name)
		{
			return string.Equals(type.Name, name, StringComparison.Ordinal)
				&& type.ContainingType is { } container
				&& string.Equals(container.ToDisplayString(), SqlTypeName, StringComparison.Ordinal);
		}
	}
}
