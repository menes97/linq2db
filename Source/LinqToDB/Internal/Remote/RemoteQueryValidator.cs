using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

		static readonly (string Key, SqlExtensionArgumentRole Role)[] _commonKeys =
		[
			(".MethodName",              SqlExtensionArgumentRole.Unrendered),
			(".ExtensionArguments.Count", SqlExtensionArgumentRole.Count),
			(".ExtensionArguments.*",     SqlExtensionArgumentRole.Text),
			("hint",                      SqlExtensionArgumentRole.Text),
		];

		const string NoneBuilder = "LinqToDB.SqlQuery.NoneExtensionBuilder";

		static readonly Dictionary<string,(string Key, SqlExtensionArgumentRole Role)[]> _builderKeys = new(StringComparer.Ordinal)
		{
			[NoneBuilder]                                                                          = _commonKeys,
			["LinqToDB.Internal.SqlProvider.HintExtensionBuilder"]                                 = _commonKeys,
			["LinqToDB.Internal.SqlProvider.HintWithParameterExtensionBuilder"]                    = [.._commonKeys, ("hintParameter", SqlExtensionArgumentRole.IdentifierOrSqlId)],
			["LinqToDB.Internal.SqlProvider.HintWithParametersExtensionBuilder"]                   = [.._commonKeys, ("hintParameters.Count", SqlExtensionArgumentRole.Count), ("hintParameters.*", SqlExtensionArgumentRole.IdentifierOrSqlId)],
			["LinqToDB.Internal.SqlProvider.HintWithFormatParametersExtensionBuilder"]             = [.._commonKeys, ("hintFormat", SqlExtensionArgumentRole.Text), ("hintParameters.Count", SqlExtensionArgumentRole.Count), ("hintParameters.*", SqlExtensionArgumentRole.IdentifierOrSqlId)],
			// renders its parameters without BuildSqlID, so a table ID is not accepted there
			["LinqToDB.Internal.DataProvider.TableSpecHintExtensionBuilder"]                       = [.._commonKeys, ("hintParameter", SqlExtensionArgumentRole.Identifier), ("hintParameters.Count", SqlExtensionArgumentRole.Count), ("hintParameters.*", SqlExtensionArgumentRole.Identifier)],
			["LinqToDB.DataProvider.ClickHouse.ClickHouseHints+TableHintExtensionBuilder"]         = _commonKeys,
			["LinqToDB.DataProvider.Ydb.YdbHints+YdbQueryHintExtensionBuilder"]                    = [.._commonKeys, ("values.Count", SqlExtensionArgumentRole.Count), ("values.*", SqlExtensionArgumentRole.Text)],
			["LinqToDB.DataProvider.PostgreSQL.PostgreSQLHints+SubQueryTableHintExtensionBuilder"] = [.._commonKeys, ("hint2", SqlExtensionArgumentRole.Text), ("tableIDs.Count", SqlExtensionArgumentRole.Count), ("tableIDs.*", SqlExtensionArgumentRole.SqlId)],
			["LinqToDB.DataProvider.MySql.MySqlHints+SubQueryTableHintExtensionBuilder"]           = [.._commonKeys, ("hint2", SqlExtensionArgumentRole.Text), ("tableIDs.Count", SqlExtensionArgumentRole.Count), ("tableIDs.*", SqlExtensionArgumentRole.SqlId)],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+WithForceSeekExtensionBuilder"]       = [.._commonKeys, ("indexName", SqlExtensionArgumentRole.Identifier), ("columns.Count", SqlExtensionArgumentRole.Count), ("columns.*", SqlExtensionArgumentRole.Element)],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+ParamsExtensionBuilder"]              = [.._commonKeys, ("values.Count", SqlExtensionArgumentRole.Count), ("values.*", SqlExtensionArgumentRole.Text)],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+TableParamsExtensionBuilder"]         = [.._commonKeys, ("tableID", SqlExtensionArgumentRole.SqlId), ("values.Count", SqlExtensionArgumentRole.Count), ("values.*", SqlExtensionArgumentRole.Text)],
			["LinqToDB.DataProvider.SqlServer.SqlServerHints+TemporalTableExtensionBuilder"]       = [.._commonKeys, ("expression", SqlExtensionArgumentRole.Text), ("dateTime", SqlExtensionArgumentRole.Element), ("dateTime2", SqlExtensionArgumentRole.Element)],
		};

		// rendered as their invariant text, which holds no character outside digits, signs, the point and the exponent
		static readonly HashSet<Type> _numericTypes =
		[
			typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
			typeof(long), typeof(ulong), typeof(decimal), typeof(float), typeof(double),
		];

		// families whose SQL builder renders a join hint, whatever the hint's builder (SqlServerSqlBuilder, ClickHouseSqlBuilder.BuildJoinType)
		static readonly HashSet<string> _joinHintFamilies = new(StringComparer.Ordinal) { "SqlServer", "ClickHouse" };

		static readonly HashSet<string> _multiWordTypes = new(StringComparer.OrdinalIgnoreCase)
		{
			"double precision", "character varying", "national character varying", "national character",
			"timestamp with time zone", "timestamp without time zone", "time with time zone", "time without time zone",
			"bit varying", "long varchar", "long raw", "binary varying",
		};

		// \z, not $: $ also matches before a final line feed
		static readonly Regex _identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
		// name, an optional size (digits or max) with an optional scale, then any number of [] array suffixes
		static readonly Regex _dbType     = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_ ]*?)\s*(\(\s*([0-9]+|[Mm][Aa][Xx])(\s*,\s*[0-9]+)?\s*\))?(\s*\[\s*\])*\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

		/// <summary>Gets a value indicating whether a query element type has an explicit disposition.</summary>
		/// <param name="type">Query element type.</param>
		/// <returns><see langword="true"/> when the type is classified as text-free or checked.</returns>
		public static bool IsClassified(QueryElementType type)
		{
			return _textFree.Contains(type) || _checked.Contains(type);
		}

		/// <summary>
		/// A DbType is rendered as written (casts, DDL), so it must be one the server's mapping defines, fit the closed type
		/// grammar, or be catalogued as <see cref="SqlTextKind.DbType"/> text.
		/// </summary>
		static void CheckDbType(RemoteQueryPolicy policy, string dbType, HashSet<string> mappedDbTypes)
		{
			if (mappedDbTypes.Contains(dbType))
				return;

			var match = _dbType.Match(dbType);
			var name  = match.Success ? match.Groups["name"].Value.Trim() : null;

			if (name != null && (!name.Contains(' ', StringComparison.Ordinal) || _multiWordTypes.Contains(name)))
				return;

			policy.CheckText(SqlTextKind.DbType, dbType, 0, "DbType");
		}

		internal static void Validate(RemoteQueryPolicy policy, IEnumerable<object> objects, SqlStatement statement, IReadOnlyCollection<string>? queryHints)
		{
			if (queryHints != null)
				foreach (var hint in queryHints)
					policy.CheckText(SqlTextKind.QueryHint, hint, 0, "QueryHints");

			var elements      = objects.OfType<IQueryElement>().ToList();
			var mappedNames   = new HashSet<string>(StringComparer.Ordinal);
			var mappedDbTypes = new HashSet<string>(policy.GetScalarDbTypes(), StringComparer.Ordinal);

			foreach (var table in elements.OfType<SqlTable>())
			{
				var descriptor = policy.GetEntityDescriptor(table.ObjectType);

				if (descriptor != null)
				{
					// a table of an inheritance hierarchy also carries the columns its sibling types map
					foreach (var column in descriptor.Columns.Concat(descriptor.InheritanceSiblingColumns))
					{
						mappedNames.Add(column.ColumnName);

						if (column.DbType != null)
							mappedDbTypes.Add(column.DbType);
					}
				}
			}

			foreach (var element in elements)
				Check(policy, element, mappedNames);

			foreach (var dbType in policy.DeferredDbTypes)
				CheckDbType(policy, dbType, mappedDbTypes);

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

				// Compiler-generated and non-ASCII names ($VB$Local_id, <Parameter>P, $VB$It) carry no meaning on the wire, so they
				// are replaced rather than rejected: a parameter is renamed the way the provider's normalizer would, and an alias is
				// dropped for the builder to generate one.
				case SqlParameter e:
					if (!IsIdentifier(e.Name, mappedNames))
						e.Name = ToIdentifier(e.Name!);
					break;

				case SelectQuery e:
					CheckIdentifier(policy, e.QueryName, "SelectQuery.QueryName", mappedNames);
					break;

				case SqlColumn e:
					if (!string.Equals(e.RawAlias, "*", StringComparison.Ordinal) && !IsIdentifier(e.RawAlias, mappedNames))
						e.RawAlias = null;
					break;

				case SqlTableSource e:
					if (e.RawAlias is not ("$" or "$F") && !IsIdentifier(e.RawAlias, mappedNames))
						e.Alias = null;
					break;

				case SqlDataType e:
					if (e.Type.DbType != null)
						policy.DeferDbType(e.Type.DbType);
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
			// an alias set by .As() carries no meaning on the wire: one outside the shape is dropped for the builder to generate one
			if (!IsIdentifier(table.Alias, mappedNames))
				table.Alias = null;

			// the builder renders Expression only for SqlTableType.Expression; it renders the name for every type, also as the
			// qualifier of an alias-less field of an expression table
			var nameIsShapeOnly = false;

			if (table.Expression != null)
			{
				if (table.SqlTableType == SqlTableType.Expression)
				{
					// a table expression gets the rendered table name as {0} and the alias as {1}, ahead of its own arguments
					policy.CheckText(SqlTextKind.TableExpression, table.Expression, 2 + (table.TableArguments?.Length ?? 0), "SqlTable");

					// an expression without {0} (SqlFn.OpenJson) has no mapped name to show; its name needs only the identifier shape
					nameIsShapeOnly = !SqlTextCatalog.UsesPlaceholder(table.Expression, 0);
				}
				else if (table.SqlTableType != SqlTableType.Function || !string.Equals(table.Expression, table.TableName.Name, StringComparison.Ordinal))
				{
					// a table function carries its name twice and renders only TableName, so Expression may only repeat it
					policy.RejectNode("SqlTable.Expression");
				}
			}

			var descriptor = policy.GetEntityDescriptor(table.ObjectType);
			var mapped     = descriptor?.Name;
			// an association to an inheritance hierarchy builds the derived type's table under the root's name
			var root       = policy.GetInheritanceRoot(descriptor)?.Name;
			var name       = table.TableName;

			CheckNamePart(policy, name.Name,     mapped?.Name,     root?.Name,     nameIsShapeOnly, mappedNames);
			CheckNamePart(policy, name.Schema,   mapped?.Schema,   root?.Schema,   nameIsShapeOnly, mappedNames);
			CheckNamePart(policy, name.Database, mapped?.Database, root?.Database, nameIsShapeOnly, mappedNames);
			CheckNamePart(policy, name.Server,   mapped?.Server,   root?.Server,   nameIsShapeOnly, mappedNames);
			CheckNamePart(policy, name.Package,  mapped?.Package,  root?.Package,  nameIsShapeOnly, mappedNames);
		}

		// each part is rendered as written, so a part the mapping does not supply needs its own catalog entry
		static void CheckNamePart(RemoteQueryPolicy policy, string? part, string? mappedPart, string? rootPart, bool shapeOnly, HashSet<string> mappedNames)
		{
			if (part == null || string.Equals(part, mappedPart, StringComparison.Ordinal) || string.Equals(part, rootPart, StringComparison.Ordinal))
				return;

			if (shapeOnly)
				CheckIdentifier(policy, part, "SqlTable.TableName", mappedNames);
			else
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
			if (!IsIdentifier(name, mappedNames))
				policy.RejectIdentifier(name!, position);
		}

		static bool IsIdentifier(string? name, HashSet<string> mappedNames)
		{
			return name == null || name.Length <= MaxIdentifierLength && (_identifier.IsMatch(name) || mappedNames.Contains(name));
		}

		// keeps the characters of the identifier shape, as UniqueParametersNormalizer does
		static string ToIdentifier(string name)
		{
			var result = new StringBuilder(Math.Min(name.Length, MaxIdentifierLength));

			foreach (var c in name)
			{
				if (result.Length == MaxIdentifierLength)
					break;

				if (c == '_' || c.IsAsciiLetter() || result.Length > 0 && c.IsAsciiDigit())
					result.Append(c);
			}

			return result.Length == 0 ? "p" : result.ToString();
		}

		static void CheckExtension(RemoteQueryPolicy policy, SqlQueryExtension extension, HashSet<string> mappedNames)
		{
			// the library table is keyed by name, so it applies only to the library's own types
			var builderType = extension.BuilderType;
			var builderName = builderType?.FullName ?? "<none>";

			if (builderType == null
				|| !(builderType.Assembly == typeof(SqlQueryExtension).Assembly && _builderKeys.TryGetValue(builderName, out var keys))
				&& !policy.TryGetBuilderRoles(builderType, out keys))
			{
				policy.RejectBuilder(builderName);
				return;
			}

			// the builder for a hint aimed at another provider renders nothing, so its arguments never reach the SQL - except a
			// join hint, which some SQL builders render themselves, and any provider type the library does not declare
			if (string.Equals(builderName, NoneBuilder, StringComparison.Ordinal)
				&& !(extension.Scope == Sql.QueryExtensionScope.JoinHint && policy.MayRenderJoinHint(_joinHintFamilies)))
			{
				return;
			}

			foreach (var pair in extension.Arguments)
			{
				var position = builderName + ":" + pair.Key;
				var (key, role) = keys.FirstOrDefault(k => KeyMatches(k.Key, pair.Key));

				if (key == null)
				{
					policy.RejectNode(position);
					continue;
				}

				switch (role)
				{
					case SqlExtensionArgumentRole.Unrendered:
					case SqlExtensionArgumentRole.Element:
						break;

					case SqlExtensionArgumentRole.Text:
						if (pair.Value is not SqlValue { Value: string or null } textValue)
							policy.RejectNode(position + " value");
						else if (textValue.Value is string text)
						{
							var bound = string.Equals(key, "hintFormat", StringComparison.Ordinal) && extension.Arguments.TryGetValue("hintParameters.Count", out var c) && c is SqlValue { Value: int count }
								? count
								: 0;

							policy.CheckText(SqlTextKind.ExtensionArgument, text, bound, position);
						}

						break;

					case SqlExtensionArgumentRole.Count:
						if (pair.Value is not SqlValue { Value: int })
							policy.RejectNode(position + " value");
						break;

					case SqlExtensionArgumentRole.SqlId:
						if (pair.Value is not SqlValue { Value: Sql.SqlID })
							policy.RejectNode(position + " value");
						break;

					case SqlExtensionArgumentRole.Identifier:
					case SqlExtensionArgumentRole.IdentifierOrSqlId:
						switch (pair.Value)
						{
							case SqlValue { Value: string identifier }:
								CheckIdentifier(policy, identifier, position, mappedNames);
								break;
							case SqlValue { Value: Sql.SqlID } when role == SqlExtensionArgumentRole.IdentifierOrSqlId:
								break;
							case SqlValue { Value: { } number } when _numericTypes.Contains(number.GetType()):
								break;
							default:
								policy.RejectNode(position + " value");
								break;
						}

						break;

					default:
						throw new InvalidOperationException();
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
			// an early check of the same type the reader is built for; LinqService checks again after the optimizer has run
			if (statement is SqlSelectStatement { SelectQuery: { } query })
				foreach (var column in query.Select.Columns)
					CheckResultType(policy, column.Expression);

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
					CheckResultType(policy, column);
		}

		static void CheckResultType(RemoteQueryPolicy policy, ISqlExpression expression)
		{
			if (policy.GetResultFieldType(expression) is { } type)
				policy.CheckResultType(type);
		}
	}
}
