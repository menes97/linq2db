using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.Remote;
using LinqToDB.SqlQuery;

namespace LinqToDB.Internal.Remote
{
	/// <summary>
	/// Closed classification of query elements received from an untrusted client.
	/// </summary>
	public static class RemoteQueryValidator
	{
		const int MaxIdentifierLength = 128;

		static readonly HashSet<QueryElementType> _textFree =
		[
			QueryElementType.SqlNullabilityExpression, QueryElementType.SqlAnchor,            QueryElementType.SqlUnaryExpression,
			QueryElementType.SqlValue,                 QueryElementType.SqlDefaultValueExpression, QueryElementType.SqlAliasPlaceholder,
			QueryElementType.SqlRow,                   QueryElementType.NotPredicate,         QueryElementType.TruePredicate,
			QueryElementType.FalsePredicate,           QueryElementType.ExprPredicate,        QueryElementType.ExprExprPredicate,
			QueryElementType.SearchStringPredicate,    QueryElementType.BetweenPredicate,     QueryElementType.IsNullPredicate,
			QueryElementType.IsDistinctPredicate,      QueryElementType.IsTruePredicate,      QueryElementType.InSubQueryPredicate,
			QueryElementType.InListPredicate,          QueryElementType.ExistsPredicate,      QueryElementType.SearchCondition,
			QueryElementType.JoinedTable,              QueryElementType.SelectClause,         QueryElementType.InsertClause,
			QueryElementType.UpdateClause,             QueryElementType.SetExpression,        QueryElementType.FromClause,
			QueryElementType.WhereClause,              QueryElementType.HavingClause,         QueryElementType.GroupByClause,
			QueryElementType.OrderByClause,            QueryElementType.OrderByItem,          QueryElementType.SetOperator,
			QueryElementType.WithClause,               QueryElementType.OutputClause,         QueryElementType.SelectStatement,
			QueryElementType.InsertStatement,          QueryElementType.InsertOrUpdateStatement, QueryElementType.UpdateStatement,
			QueryElementType.DeleteStatement,          QueryElementType.MultiInsertStatement, QueryElementType.ConditionalInsertClause,
			QueryElementType.DropTableStatement,       QueryElementType.TruncateTableStatement, QueryElementType.SqlTableLikeSource,
			QueryElementType.MergeOperationClause,     QueryElementType.GroupingSet,          QueryElementType.Comment,
			QueryElementType.SqlCast,                  QueryElementType.SqlCoalesce,          QueryElementType.SqlCondition,
			QueryElementType.SqlCase,                  QueryElementType.CompareTo,            QueryElementType.SqlFunctionArgument,
			QueryElementType.SqlWindowOrderItem,       QueryElementType.SqlFrameClause,       QueryElementType.SqlFrameBoundary,
			QueryElementType.SqlConcat,                QueryElementType.SqlCteTableField,     QueryElementType.SqlKeepClause,
			QueryElementType.SqlParameterCast,         QueryElementType.SqlInterval,          QueryElementType.SqlIntervalDifference,
			QueryElementType.SqlIntervalPart,          QueryElementType.SqlTemporalArithmetic, QueryElementType.SqlValuesTable,
		];

		static readonly HashSet<QueryElementType> _checked =
		[
			QueryElementType.SqlField,        QueryElementType.SqlFunction,    QueryElementType.SqlParameter,
			QueryElementType.SqlExpression,   QueryElementType.SqlBinaryExpression, QueryElementType.SqlDataType,
			QueryElementType.SqlTable,        QueryElementType.LikePredicate,  QueryElementType.SqlQuery,
			QueryElementType.Column,          QueryElementType.TableSource,    QueryElementType.CteClause,
			QueryElementType.SqlCteTable,     QueryElementType.SqlRawSqlTable, QueryElementType.MergeStatement,
			QueryElementType.CreateTableStatement, QueryElementType.SqlQueryExtension, QueryElementType.SqlFragment,
			QueryElementType.SqlExtendedFunction,  QueryElementType.SqlCteField,
		];

		static readonly HashSet<string> _operators = new(StringComparer.Ordinal)
		{
			"+", "-", "*", "/", "%", "&", "|", "^", "||",
		};

		static readonly string[] _commonKeys = [".MethodName", ".ExtensionArguments.Count", ".ExtensionArguments.*", "hint"];

		const string NoneBuilder = "LinqToDB.SqlQuery.NoneExtensionBuilder";

		static readonly Dictionary<string,string[]> _builderKeys = new(StringComparer.Ordinal)
		{
			[NoneBuilder]                                                                              = [],
			["LinqToDB.Internal.SqlProvider.HintExtensionBuilder"]                                     = _commonKeys,
			["LinqToDB.Internal.SqlProvider.HintWithParameterExtensionBuilder"]                        = [.._commonKeys, "hintParameter"],
			["LinqToDB.Internal.SqlProvider.HintWithParametersExtensionBuilder"]                       = [.._commonKeys, "hintParameters.Count", "hintParameters.*"],
			["LinqToDB.Internal.SqlProvider.HintWithFormatParametersExtensionBuilder"]                 = [.._commonKeys, "hintFormat", "hintParameters.Count", "hintParameters.*"],
			["LinqToDB.Internal.DataProvider.TableSpecHintExtensionBuilder"]                           = [.._commonKeys, "hintParameter", "hintParameters.Count", "hintParameters.*"],
			["LinqToDB.DataProvider.ClickHouse.ClickHouseHints+TableHintExtensionBuilder"]             = _commonKeys,
			["LinqToDB.DataProvider.Ydb.YdbHints+YdbQueryHintExtensionBuilder"]                        = [.._commonKeys, "values.Count", "values.*"],
			["LinqToDB.DataProvider.PostgreSQL.PostgreSQLHints+SubQueryTableHintExtensionBuilder"]     = [.._commonKeys, "hint2", "tableIDs.Count", "tableIDs.*"],
			["LinqToDB.DataProvider.MySql.MySqlHints+SubQueryTableHintExtensionBuilder"]               = [.._commonKeys, "hint2", "tableIDs.Count", "tableIDs.*"],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+WithForceSeekExtensionBuilder"]           = [.._commonKeys, "indexName", "columns.Count", "columns.*"],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+ParamsExtensionBuilder"]                  = [.._commonKeys, "values.Count", "values.*"],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+TableParamsExtensionBuilder"]             = [.._commonKeys, "tableID", "values.Count", "values.*"],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+TemporalTableExtensionBuilder"]           = [.._commonKeys, "expression", "dateTime", "dateTime2"],
		};

		static readonly HashSet<string> _textKeys       = new(StringComparer.Ordinal) { ".ExtensionArguments.*", "hint", "hint2", "hintFormat", "expression", "values.*" };
		static readonly HashSet<string> _identifierKeys = new(StringComparer.Ordinal) { "hintParameter", "hintParameters.*", "indexName" };

		static readonly HashSet<string> _multiWordTypes = new(StringComparer.OrdinalIgnoreCase)
		{
			"double precision", "character varying", "national character varying", "national character",
			"timestamp with time zone", "timestamp without time zone", "time with time zone", "time without time zone",
			"bit varying", "long varchar", "long raw", "binary varying",
		};

		static readonly Regex _identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
		static readonly Regex _dbType     = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_ ]*?)\s*(\(\s*[0-9]+(\s*,\s*[0-9]+)?\s*\))?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

		/// <summary>Gets a value indicating whether a query element type has an explicit disposition.</summary>
		/// <param name="type">Query element type.</param>
		/// <returns><see langword="true"/> when the type is classified as text-free or checked.</returns>
		public static bool IsClassified(QueryElementType type)
		{
			return _textFree.Contains(type) || _checked.Contains(type);
		}

		internal static void CheckDbType(RemoteQueryPolicy policy, string? dbType)
		{
			if (dbType == null)
				return;

			var match = _dbType.Match(dbType);
			var name  = match.Success ? match.Groups["name"].Value.Trim() : null;

			if (name == null || (name.Contains(' ', StringComparison.Ordinal) && !_multiWordTypes.Contains(name)))
				policy.RejectIdentifier(dbType, "DbType");
		}

		internal static void Validate(RemoteQueryPolicy policy, IEnumerable<object> objects, SqlStatement statement, IReadOnlyCollection<string>? queryHints)
		{
			if (queryHints != null)
				foreach (var hint in queryHints)
					policy.CheckText(SqlTextKind.QueryHint, hint, 0, "QueryHints");

			var elements    = objects.OfType<IQueryElement>().ToList();
			var mappedNames = new HashSet<string>(StringComparer.Ordinal);

			foreach (var table in elements.OfType<SqlTable>())
			{
				var descriptor = policy.GetEntityDescriptor(table.ObjectType);

				if (descriptor != null)
					foreach (var column in descriptor.Columns)
						mappedNames.Add(column.ColumnName);
			}

			foreach (var element in elements)
				Check(policy, element, mappedNames);

			statement.Tag = null;

			CheckResultColumns(policy, statement);
		}

		static void Check(RemoteQueryPolicy policy, IQueryElement element, HashSet<string> mappedNames)
		{
			if (_textFree.Contains(element.ElementType))
				return;

			switch (element)
			{
				case SqlExpression e       : policy.CheckText(SqlTextKind.Expression, e.Expr, e.Parameters.Length, "SqlExpression");           break;
				case SqlFragment e         : policy.CheckText(SqlTextKind.Fragment, e.Expr, e.Parameters.Length, "SqlFragment");               break;
				case SqlFunction e         : policy.CheckText(SqlTextKind.Function, e.Name, int.MaxValue, "SqlFunction");                      break;
				case SqlExtendedFunction e : policy.CheckText(SqlTextKind.Function, e.FunctionName, int.MaxValue, "SqlExtendedFunction");      break;

				case SqlBinaryExpression e:
					if (!_operators.Contains(e.Operation))
						policy.CheckText(SqlTextKind.Operator, e.Operation, 0, "SqlBinaryExpression");
					break;

				case SqlPredicate.Like e:
					if (e.FunctionName != null)
						policy.CheckText(SqlTextKind.Function, e.FunctionName, int.MaxValue, "LikePredicate");
					break;

				case SqlRawSqlTable e:
					policy.CheckText(SqlTextKind.RawSqlTable, e.SQL, e.Parameters.Length, "SqlRawSqlTable");
					CheckIdentifier(policy, e.Alias, "SqlRawSqlTable.Alias", mappedNames);
					break;

				case SqlCteTable e:
					CheckIdentifier(policy, e.Alias, "SqlCteTable.Alias", mappedNames);
					break;

				case SqlTable e:
					CheckTable(policy, e, mappedNames);
					break;

				case SqlField e:
					CheckField(policy, e, mappedNames);
					break;

				case SqlCteField e:
					CheckIdentifier(policy, e.Name, "SqlCteField.Name", mappedNames);
					break;

				case CteClause e:
					CheckIdentifier(policy, e.Name, "CteClause.Name", mappedNames);
					break;

				case SqlParameter e:
					CheckIdentifier(policy, e.Name, "SqlParameter.Name", mappedNames);
					break;

				case SelectQuery e:
					CheckIdentifier(policy, e.QueryName, "SelectQuery.QueryName", mappedNames);
					break;

				case SqlColumn e:
					if (!string.Equals(e.RawAlias, "*", StringComparison.Ordinal))
						CheckIdentifier(policy, e.RawAlias, "SqlColumn.Alias", mappedNames);
					break;

				case SqlTableSource e:
					if (e.RawAlias is not ("$" or "$F"))
						CheckIdentifier(policy, e.RawAlias, "SqlTableSource.Alias", mappedNames);
					break;

				case SqlDataType e:
					CheckDbType(policy, e.Type.DbType);
					break;

				case SqlMergeStatement e:
					if (e.Hint != null)
						policy.CheckText(SqlTextKind.MergeHint, e.Hint, 0, "SqlMergeStatement");
					break;

				case SqlCreateTableStatement e:
					if (e.StatementHeader != null)
						policy.CheckText(SqlTextKind.StatementHeader, e.StatementHeader, 1, "SqlCreateTableStatement");
					if (e.StatementFooter != null)
						policy.CheckText(SqlTextKind.StatementFooter, e.StatementFooter, 0, "SqlCreateTableStatement");
					break;

				case SqlQueryExtension e:
					CheckExtension(policy, e, mappedNames);
					break;

				default:
					policy.RejectNode(element.ElementType.ToString());
					break;
			}
		}

		static void CheckTable(RemoteQueryPolicy policy, SqlTable table, HashSet<string> mappedNames)
		{
			CheckIdentifier(policy, table.Alias, "SqlTable.Alias", mappedNames);

			// a table expression gets the rendered table name as {0} and the alias as {1}, ahead of its own arguments
			if (table.Expression != null)
				policy.CheckText(SqlTextKind.TableExpression, table.Expression, 2 + (table.TableArguments?.Length ?? 0), "SqlTable");

			var mapped = policy.GetEntityDescriptor(table.ObjectType)?.Name;
			var name   = table.TableName;

			CheckNamePart(policy, name.Name,     mapped?.Name);
			CheckNamePart(policy, name.Schema,   mapped?.Schema);
			CheckNamePart(policy, name.Database, mapped?.Database);
			CheckNamePart(policy, name.Server,   mapped?.Server);
			CheckNamePart(policy, name.Package,  mapped?.Package);
		}

		// each part is rendered as written, so a part the mapping does not supply needs its own catalog entry
		static void CheckNamePart(RemoteQueryPolicy policy, string? part, string? mappedPart)
		{
			if (part != null && !string.Equals(part, mappedPart, StringComparison.Ordinal))
				policy.CheckText(SqlTextKind.TableName, part, 0, "SqlTable");
		}

		static void CheckField(RemoteQueryPolicy policy, SqlField field, HashSet<string> mappedNames)
		{
			if (field.CreateFormat != null)
				policy.RejectNode("SqlField.CreateFormat");

			if (string.Equals(field.Name, "*", StringComparison.Ordinal) && string.Equals(field.PhysicalName, "*", StringComparison.Ordinal))
				return;

			var isMappedOwner = field.Table is SqlTable { ObjectType: not null } && !field.IsDynamic;

			if (isMappedOwner)
			{
				if (!mappedNames.Contains(field.PhysicalName))
					policy.RejectIdentifier(field.PhysicalName, "SqlField.PhysicalName");

				return;
			}

			CheckIdentifier(policy, field.PhysicalName, "SqlField.PhysicalName", mappedNames);
		}

		static void CheckIdentifier(RemoteQueryPolicy policy, string? name, string position, HashSet<string> mappedNames)
		{
			if (name == null)
				return;

			if (name.Length > MaxIdentifierLength || (!_identifier.IsMatch(name) && !mappedNames.Contains(name)))
				policy.RejectIdentifier(name, position);
		}

		static void CheckExtension(RemoteQueryPolicy policy, SqlQueryExtension extension, HashSet<string> mappedNames)
		{
			if (extension.BuilderType?.FullName is not { } builderName || !_builderKeys.TryGetValue(builderName, out var keys))
			{
				policy.RejectBuilder(extension.BuilderType?.FullName ?? "<none>");
				return;
			}

			// the builder for a hint aimed at another provider renders nothing, so its arguments never reach the SQL
			if (string.Equals(builderName, NoneBuilder, StringComparison.Ordinal))
				return;

			foreach (var pair in extension.Arguments)
			{
				var pattern = keys.FirstOrDefault(k => KeyMatches(k, pair.Key));

				if (pattern == null)
				{
					policy.RejectNode(builderName + ":" + pair.Key);
					continue;
				}

				if (pair.Value is not SqlValue { Value: string text })
					continue;

				if (_textKeys.Contains(pattern))
				{
					var bound = string.Equals(pattern, "hintFormat", StringComparison.Ordinal) && extension.Arguments.TryGetValue("hintParameters.Count", out var c) && c is SqlValue { Value: int count }
						? count
						: 0;

					policy.CheckText(SqlTextKind.ExtensionArgument, text, bound, builderName + ":" + pair.Key);
				}
				else if (_identifierKeys.Contains(pattern))
				{
					CheckIdentifier(policy, text, builderName + ":" + pair.Key, mappedNames);
				}
			}
		}

		static bool KeyMatches(string pattern, string key)
		{
			if (!pattern.EndsWith(".*", StringComparison.Ordinal))
				return string.Equals(pattern, key, StringComparison.Ordinal);

			var prefixLength = pattern.Length - 1;

			return key.Length > prefixLength
				&& string.CompareOrdinal(pattern, 0, key, 0, prefixLength) == 0
				&& key.Skip(prefixLength).All(static c => c is >= '0' and <= '9');
		}

		static void CheckResultColumns(RemoteQueryPolicy policy, SqlStatement statement)
		{
			if (statement is SqlSelectStatement { SelectQuery: { } query })
				foreach (var column in query.Select.Columns)
					CheckResultType(policy, column.SystemType);

			var output = statement switch
			{
				SqlInsertStatement s => s.Output,
				SqlUpdateStatement s => s.Output,
				SqlDeleteStatement s => s.Output,
				SqlMergeStatement  s => s.Output,
				_                    => null,
			};

			if (output?.OutputColumns != null)
				foreach (var column in output.OutputColumns)
					CheckResultType(policy, column.SystemType);
		}

		static void CheckResultType(RemoteQueryPolicy policy, Type? type)
		{
			if (type == null || type == typeof(object))
				return;

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
				type = type.GetGenericArguments()[0];

			policy.CheckResultType(type);
		}
	}
}
