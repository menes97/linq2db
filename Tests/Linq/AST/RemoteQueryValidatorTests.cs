using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

using LinqToDB;
using LinqToDB.Common;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Internal.Remote;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.Remote;
using LinqToDB.SqlQuery;

using NUnit.Framework;

using Shouldly;

[assembly: SqlTextCatalog(typeof(Tests.AST.RemoteQueryValidatorTests.MappingAssemblyCatalog))]

namespace Tests.AST
{
	[TestFixture]
	public class RemoteQueryValidatorTests : TestBase
	{
		sealed class RecordingValue
		{
			public static int Constructed;

			readonly string _value;

			public RecordingValue(string value)
			{
				Constructed++;
				_value = value;
			}

			public override string ToString() => _value;
		}

		struct UnregisteredStruct
		{
			public int Value;

			public UnregisteredStruct(string value)
			{
				Value = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
			}

			public override readonly string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		enum LocalEnum
		{
			One = 1,
		}

		sealed class LocalEntity
		{
			public int Id { get; set; }
		}

		sealed class LocalBuilder : ISqlQueryExtensionBuilder
		{
			public void Build(NullabilityContext nullability, ISqlBuilder sqlBuilder, System.Text.StringBuilder stringBuilder, SqlQueryExtension sqlQueryExtension)
			{
			}
		}

		static readonly Assembly _thisAssembly = typeof(RemoteQueryValidatorTests).Assembly;
		static readonly Type     _hintBuilder  = typeof(DataOptions).Assembly.GetType("LinqToDB.Internal.SqlProvider.HintExtensionBuilder", throwOnError: true)!;

		static DataConnection CreateConnection(MappingSchema? schema = null)
		{
			var options = new DataOptions().UseSQLite("Data Source=:memory:", SQLiteProvider.Microsoft);

			return schema == null ? new DataConnection(options) : new DataConnection(options.UseMappingSchema(schema));
		}

		static (MethodInfo Serialize, MethodInfo Deserialize, MappingSchema Schema) GetSerializer()
		{
			var assembly   = typeof(DataOptions).Assembly;
			var serializer = assembly.GetType("LinqToDB.Internal.Remote.LinqServiceSerializer", throwOnError: true)!;
			var schema     = (MappingSchema)assembly.GetType("LinqToDB.Internal.Remote.SerializationMappingSchema", throwOnError: true)!.GetField("Instance")!.GetValue(null)!;

			var serialize   = serializer.GetMethod("Serialize", [typeof(MappingSchema), typeof(SqlStatement), typeof(IReadOnlyParameterValues), typeof(IReadOnlyCollection<string>), typeof(DataOptions)])!;
			var deserialize = serializer.GetMethod("Deserialize", [typeof(MappingSchema), typeof(MappingSchema), typeof(DataOptions), typeof(string), typeof(RemoteQueryPolicy)])!;

			return (serialize, deserialize, schema);
		}

		static LinqServiceQuery RoundTrip(DataConnection db, SqlStatement statement, RemoteQueryPolicy? policy)
		{
			var (serialize, deserialize, schema) = GetSerializer();

			var data = (string)serialize.Invoke(null, [schema, statement, null, null, db.Options])!;

			try
			{
				return (LinqServiceQuery)deserialize.Invoke(null, [schema, db.MappingSchema, db.Options, data, policy])!;
			}
			catch (TargetInvocationException ex)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
				throw;
			}
		}

		static SqlSelectStatement SelectOf(params ISqlExpression[] expressions)
		{
			var query = new SelectQuery();

			foreach (var expression in expressions)
				query.Select.Add(expression);

			return new SqlSelectStatement(query);
		}

		static RemoteQueryPolicy Untrusted(DataConnection db, params Assembly[] mappingAssemblies)
		{
			return RemoteQueryPolicy.Create(db, mappingAssemblies, null);
		}

		static RemoteRejection RejectedSingle(DataConnection db, SqlStatement statement, SqlTextCatalog? catalog = null)
		{
			var policy    = RemoteQueryPolicy.Create(db, [], null, catalog);
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, policy))!;

			return exception.Rejections[0];
		}

		const string InertText = "NOT_IN_CATALOG({0})";

		static SqlValue One => new(1);

		[Test]
		public void ExpressionTextOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),InertText, One)));

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
			rejection.Kind.ShouldBe(SqlTextKind.Expression);
		}

		[Test]
		public void FragmentTextOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),"{0}", new SqlFragment(InertText, One))));

			rejection.Kind.ShouldBe(SqlTextKind.Fragment);
		}

		[Test]
		public void FunctionNameOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var rejection = RejectedSingle(db, SelectOf(new SqlFunction(new DbDataType(typeof(int)), "NOT_IN_CATALOG", One)));

			rejection.Kind.ShouldBe(SqlTextKind.Function);
		}

		[Test]
		public void ExpressionInCatalogIsAcceptedWithRenumberedPlaceholder()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, "IN_CATALOG({1})");

			var statement = SelectOf(new SqlExpression(new DbDataType(typeof(int)),"IN_CATALOG({0})", One));

			RoundTrip(db, statement, RemoteQueryPolicy.Create(db, [], null, catalog));
		}

		[Test]
		public void PlaceholderBeyondArgumentsIsRejected()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, "IN_CATALOG({0})");

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),"IN_CATALOG({3})", One)), catalog);

			rejection.Reason.ShouldBe(RemoteRejectionReason.PlaceholderOutOfRange);
		}

		[Test]
		public void PlaceholderIndexTooLargeToCountIsRejected()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, "IN_CATALOG({0})");

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),"IN_CATALOG({1000000})", One)), catalog);

			rejection.Reason.ShouldBe(RemoteRejectionReason.PlaceholderOutOfRange);
		}

		[Test]
		public void NonAsciiDigitIsNotAPlaceholder()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, "IN_CATALOG({0})");

			// ARABIC-INDIC DIGIT ZERO: char.IsDigit accepts it, String.Format does not
			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),"IN_CATALOG({٠})", One)), catalog);

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
		}

		[Test]
		public void TextApprovedForOtherKindIsRejected()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.QueryHint, InertText);

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),InertText, One)), catalog);

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextAllowedForOtherKind);
		}

		[Test]
		public void RemovalWinsOverAddition()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, InertText);
			catalog.Remove(SqlTextKind.Expression, InertText);

			var rejection = RejectedSingle(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)),InertText, One)), catalog);

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextRemovedFromCatalog);
		}

		[Test]
		public void UnknownOperatorIsRejected()
		{
			using var db = CreateConnection();

			var rejection = RejectedSingle(db, SelectOf(new SqlBinaryExpression(typeof(int), One, "NOT_AN_OPERATOR", One)));

			rejection.Kind.ShouldBe(SqlTextKind.Operator);
		}

		[Test]
		public void QueryNameOutsideShapeIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			statement.SelectQuery!.QueryName = "not a name";

			var rejection = RejectedSingle(db, statement);

			rejection.Reason.ShouldBe(RemoteRejectionReason.IdentifierNotMapped);
		}

		[Test]
		public void QueryNameInShapeIsAccepted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			statement.SelectQuery!.QueryName = "query_1";

			RoundTrip(db, statement, Untrusted(db));
		}

		[Test]
		public void DbTypeOutsideShapeIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(int), DataType.Int32, "not a type"), 1));

			var rejection = RejectedSingle(db, statement);

			rejection.Reason.ShouldBe(RemoteRejectionReason.IdentifierNotMapped);
		}

		[Test]
		public void DbTypeInShapeIsAccepted([Values("int", "varchar(10)", "decimal(18, 4)", "double precision")] string dbType)
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(int), DataType.Int32, dbType), 1));

			RoundTrip(db, statement, Untrusted(db));
		}

		[Test]
		public void EveryQueryElementTypeIsClassified([Values] QueryElementType type)
		{
			var notOnWire = type is QueryElementType.SqlObjectExpression
				or QueryElementType.SqlExtension
				or QueryElementType.SqlInlinedExpression
				or QueryElementType.SqlInlinedToSqlExpression
				or QueryElementType.SqlObjectNameExpression
				or QueryElementType.SqlRawStatement;

			var isClassified = RemoteQueryValidator.IsClassified(type);

			isClassified.ShouldBe(!notOnWire);
		}

		[Test]
		public void TagIsDropped()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			var comment = new SqlComment();

			comment.Lines.Add("inert");

			typeof(SqlStatement).GetProperty(nameof(SqlStatement.Tag))!.SetValue(statement, comment);

			var result = RoundTrip(db, statement, Untrusted(db));

			result.Statement.Tag.ShouldBeNull();
		}

		[Test]
		public void QueryHintOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var (serialize, deserialize, schema) = GetSerializer();

			var data = (string)serialize.Invoke(null, [schema, SelectOf(One), null, new[] { "NOT_IN_CATALOG" }, db.Options])!;

			var exception = Assert.Throws<TargetInvocationException>(() => deserialize.Invoke(null, [schema, db.MappingSchema, db.Options, data, Untrusted(db)]))!;

			exception.InnerException.ShouldBeOfType<RemoteRequestRejectedException>();
		}

		[Test]
		public void ExtensionArgumentKeyOutsideTableIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			statement.SelectQuery!.SqlQueryExtensions =
			[
				new SqlQueryExtension
				{
					Scope       = Sql.QueryExtensionScope.QueryHint,
					BuilderType = _hintBuilder,
					Arguments   = new(StringComparer.Ordinal) { ["unknown"] = One },
				},
			];

			var rejection = RejectedSingle(db, statement);

			rejection.Reason.ShouldBe(RemoteRejectionReason.NodeNotAllowed);
		}

		[Test]
		public void HintTextOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			statement.SelectQuery!.SqlQueryExtensions =
			[
				new SqlQueryExtension
				{
					Scope       = Sql.QueryExtensionScope.QueryHint,
					BuilderType = _hintBuilder,
					Arguments   = new(StringComparer.Ordinal) { ["hint"] = new SqlValue(InertText) },
				},
			];

			var rejection = RejectedSingle(db, statement);

			rejection.Kind.ShouldBe(SqlTextKind.ExtensionArgument);
		}

		[Test]
		public void ResultColumnTypeIsRejected()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, InertText);

			var statement = SelectOf(new SqlExpression(new DbDataType(typeof(LocalEntity)), InertText, One));

			var policy    = RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog);
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, policy))!;

			exception.Rejections[0].NodeType.ShouldBe("ResultColumn");
		}

		[Test]
		public void ValueTypeOutsideAllowlistIsRejectedBeforeConversion()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(string)), new RecordingValue("x")));

			RecordingValue.Constructed = 0;

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			RecordingValue.Constructed.ShouldBe(0);
			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TypeNotAllowed);
		}

		[Test]
		public void ValueTypeIsConvertedWhenTrusted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(string)), new RecordingValue("x")));

			RecordingValue.Constructed = 0;

			RoundTrip(db, statement, null);

			RecordingValue.Constructed.ShouldBe(1);
		}

		[Test]
		public void ScalarValuesAreAccepted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(
				new SqlValue(1),
				new SqlValue("text"),
				new SqlValue(typeof(int?), null),
				new SqlValue(new byte[] { 1 }));

			RoundTrip(db, statement, Untrusted(db));
		}

		[Test]
		public void EnumValueFromMappingAssemblyIsAccepted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(LocalEnum.One));

			RoundTrip(db, statement, Untrusted(db, _thisAssembly));
		}

		[Test]
		public void BuilderOutsideKnownSetIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(1));

			statement.SelectQuery!.SqlQueryExtensions = [new SqlQueryExtension { Scope = Sql.QueryExtensionScope.QueryHint, BuilderType = typeof(LocalBuilder), Arguments = new(StringComparer.Ordinal) }];

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.BuilderNotAllowed);
		}

		[Test]
		public void TypeAllowedAsObjectIsRejectedAsValue()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new LocalEntity()));

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db, _thisAssembly)))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TypeNotAllowed);
		}

		[Test]
		public void CoreClassUsedAsObjectIsNulled()
		{
			using var db = CreateConnection();

			var query = new SelectQuery();
			var table = new SqlTable(db.MappingSchema.GetEntityDescriptor(typeof(Version)));

			query.From.Tables.Add(new SqlTableSource(table, "t"));
			query.Select.Add(new SqlValue(1));

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableName, "Version");

			var result = RoundTrip(db, new SqlSelectStatement(query), RemoteQueryPolicy.Create(db, [], null, catalog));
			var source = (SqlTable)result.Statement.SelectQuery!.From.Tables[0].Source;

			source.ObjectType.ShouldBeNull();
		}

		static SqlSelectStatement SelectFrom(SqlTable table)
		{
			var query = new SelectQuery();

			query.From.Tables.Add(new SqlTableSource(table, "t"));
			query.Select.Add(new SqlValue(1));

			return new SqlSelectStatement(query);
		}

		const string TableExpression = "{0} {1}";

		static SqlTable MappedTable(DataConnection db) => new(db.MappingSchema.GetEntityDescriptor(typeof(LocalEntity)));

		[Test]
		public void MappedTableWithCatalogedExpressionIsAccepted()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, TableExpression);

			var table = MappedTable(db);

			table.Expression = TableExpression;

			RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog));
		}

		[Test]
		public void TableNameIsCheckedWhenTableExpressionIsSet()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, TableExpression);

			var table = MappedTable(db);

			// the expression renders the table name into {0}, so an unmapped name must not ride along with a catalogued expression
			table.Expression = TableExpression;
			table.TableName  = new SqlObjectName("NOT_A_MAPPED_NAME");

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog)))!;

			exception.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "NOT_A_MAPPED_NAME");
		}

		[Test]
		public void TableNameIsNotCheckedWhenTableExpressionDoesNotRenderIt()
		{
			using var db = CreateConnection();

			const string expression = "NOT_THE_NAME({2}) {1}";

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, expression);

			var table = MappedTable(db);

			// like SqlFn.OpenJson: the expression has no {0}, so the table name never reaches the SQL
			table.SqlTableType   = SqlTableType.Expression;
			table.Expression     = expression;
			table.TableName      = new SqlObjectName("NOT_A_MAPPED_NAME");
			table.TableArguments = [One];

			RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog));
		}

		[Test]
		public void TableNameIsCheckedWhenTableExpressionRendersItWithSpaces([Values("{0 } {1}", "{ 0} {1}", "{0,5} {1}")] string expression)
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, expression);

			var table = MappedTable(db);

			table.SqlTableType = SqlTableType.Expression;
			table.Expression   = expression;
			table.TableName    = new SqlObjectName("NOT_A_MAPPED_NAME");

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog)))!;

			exception.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "NOT_A_MAPPED_NAME");
		}

		[Test]
		public void EveryPartOfAnOverriddenTableNameIsChecked([Values("Schema", "Database", "Server")] string part)
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableName, "Allowed_Name");

			var table = MappedTable(db);

			table.TableName = part switch
			{
				"Schema"   => new SqlObjectName("Allowed_Name", Schema  : "NOT_ALLOWED"),
				"Database" => new SqlObjectName("Allowed_Name", Database: "NOT_ALLOWED"),
				_          => new SqlObjectName("Allowed_Name", Server  : "NOT_ALLOWED"),
			};

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog)))!;

			exception.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "NOT_ALLOWED");
		}

		[Test]
		public void AllowedTableNameOverrideIsAccepted()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableName, "Allowed_Name");
			catalog.Add(SqlTextKind.TableName, "Allowed_Schema");

			var table = MappedTable(db);

			table.TableName = new SqlObjectName("Allowed_Name", Schema: "Allowed_Schema");

			RoundTrip(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog));
		}

		static RemoteRequestRejectedException Rejected(DataConnection db, SqlStatement statement)
		{
			return Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, RemoteQueryPolicy.Create(db, [_thisAssembly], null)))!;
		}

		[Test]
		public void RawSqlTableTextOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var raw   = new SqlRawSqlTable(db.MappingSchema.GetEntityDescriptor(typeof(LocalEntity)), InertText, false, [One]);
			var query = new SelectQuery();

			query.From.Tables.Add(new SqlTableSource(raw, "r"));
			query.Select.Add(One);

			Rejected(db, new SqlSelectStatement(query)).Rejections.ShouldContain(static r => r.Kind == SqlTextKind.RawSqlTable && r.Text == InertText);
		}

		[Test]
		public void CreateTableTextOutsideCatalogIsRejected([Values("Header", "Footer")] string position)
		{
			using var db = CreateConnection();

			var statement = new SqlCreateTableStatement(MappedTable(db));

			if (position == "Header")
				statement.StatementHeader = "NOT_IN_CATALOG {0}";
			else
				statement.StatementFooter = "NOT_IN_CATALOG";

			var kind = position == "Header" ? SqlTextKind.StatementHeader : SqlTextKind.StatementFooter;

			Rejected(db, statement).Rejections.ShouldContain(r => r.Kind == kind);
		}

		[Test]
		public void FieldCreateFormatIsRejected()
		{
			using var db = CreateConnection();

			var table = MappedTable(db);

			table.Fields[0].CreateFormat = "NOT_IN_CATALOG";

			Rejected(db, new SqlCreateTableStatement(table)).Rejections.ShouldContain(static r => r.NodeType == "SqlField.CreateFormat");
		}

		[Test]
		public void MergeHintOutsideCatalogIsRejected()
		{
			using var db = CreateConnection();

			var statement = new SqlMergeStatement(MappedTable(db));

			typeof(SqlMergeStatement).GetProperty(nameof(SqlMergeStatement.Hint))!.SetValue(statement, "NOT_IN_CATALOG");

			Rejected(db, statement).Rejections.ShouldContain(static r => r.Kind == SqlTextKind.MergeHint);
		}

		[Test]
		public void UnmappedFieldOfMappedTableIsRejected()
		{
			using var db = CreateConnection();

			var table = MappedTable(db);
			var field = new SqlField(new DbDataType(typeof(int)), "Unmapped", true) { PhysicalName = "NOT_MAPPED" };

			table.Add(field);

			var query = new SelectQuery();

			query.From.Tables.Add(new SqlTableSource(table, "t"));
			query.Select.Add(field);

			Rejected(db, new SqlSelectStatement(query)).Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.IdentifierNotMapped && r.Text == "NOT_MAPPED");
		}

		[Test]
		public void UndefinedRenderedEnumValueIsRejected()
		{
			using var db = CreateConnection();

			var query  = new SelectQuery();
			var source = new SqlTableSource(MappedTable(db), "t");
			var joined = new SqlTableSource(MappedTable(db), "u");

			source.Joins.Add(new SqlJoinedTable((JoinType)999, joined, false));
			query.From.Tables.Add(source);
			query.Select.Add(One);

			Rejected(db, new SqlSelectStatement(query)).Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.InvalidEnumValue);
		}

		[Test]
		public void RejectionsAreCappedAndMarkedTruncated()
		{
			using var db = CreateConnection();

			var expressions = new ISqlExpression[150];

			for (var i = 0; i < expressions.Length; i++)
				expressions[i] = new SqlExpression(new DbDataType(typeof(int)), $"NOT_IN_CATALOG_{i}({{0}})", One);

			var exception = Rejected(db, SelectOf(expressions));

			exception.Rejections.Count.ShouldBe(100);
			exception.IsTruncated.ShouldBeTrue();
		}

		[Test]
		public void MessageEscapesTextAndListsNoCatalogEntry()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, "CATALOG_ONLY_TEXT({0})");

			// newline, bidi override and line separator would split or spoof a log line
			var text      = "NOT_IN_CATALOG\n\u202E\u2028({0})" + new string('x', 600);
			var policy    = RemoteQueryPolicy.Create(db, [], null, catalog);
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)), text, One)), policy))!;

			exception.Message.ShouldContain("\\u000a");
			exception.Message.ShouldContain("\\u202e");
			exception.Message.ShouldContain("\\u2028");
			exception.Message.ShouldNotContain("\u202E");
			exception.Message.ShouldNotContain("\u2028");
			exception.Message.ShouldContain("...");
			exception.Message.ShouldNotContain(new string('x', 600));
			exception.Message.ShouldNotContain("CATALOG_ONLY_TEXT");
		}

		static object? ReadCount(DataConnection db, string data)
		{
			var type         = typeof(DataOptions).Assembly.GetType("LinqToDB.Internal.Remote.LinqServiceSerializer+QueryDeserializer", throwOnError: true)!;
			var deserializer = Activator.CreateInstance(type, GetSerializer().Schema, db.MappingSchema, db.Options, Untrusted(db))!;
			var flags        = BindingFlags.NonPublic | BindingFlags.Instance;

			type.BaseType!.GetField("Str", flags)!.SetValue(deserializer, data);
			type.BaseType!.GetField("Pos", flags)!.SetValue(deserializer, 0);

			try
			{
				return type.BaseType!.GetMethod("ReadCount", flags)!.Invoke(deserializer, null);
			}
			catch (TargetInvocationException ex)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
				throw;
			}
		}

		[Test]
		public void ElementCountBeyondRequestLengthIsRejectedBeforeAllocation()
		{
			using var db = CreateConnection();

			// control: a count the remaining input can hold is read as is
			ReadCount(db, " 3 abc").ShouldBe(3);

			Assert.Throws<LinqToDBException>(() => ReadCount(db, " 2000000000 abc"));
		}

		[Test]
		public void OutputColumnTypeIsRejected([Values("Insert", "Update", "Delete", "Merge")] string statementKind)
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.Expression, InertText);

			var output = new SqlOutputClause { OutputColumns = [new SqlExpression(new DbDataType(typeof(LocalEntity)), InertText, One)] };

			SqlStatement statement = statementKind switch
			{
				"Insert" => new SqlInsertStatement { Output = output },
				"Update" => new SqlUpdateStatement(new SelectQuery()) { Output = output },
				"Delete" => new SqlDeleteStatement { Output = output },
				_        => new SqlMergeStatement(MappedTable(db)) { Output = output },
			};

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog)))!;

			exception.Rejections.ShouldContain(static r => r.NodeType == "ResultColumn");
		}

		static SqlSelectStatement WithIdentifier(DataConnection db, string position, string name)
		{
			var table  = MappedTable(db);
			var query  = new SelectQuery();

			if (position == "TableAlias")
				table.Alias = name;

			query.From.Tables.Add(new SqlTableSource(table, position == "TableSourceAlias" ? name : "t"));
			query.Select.Add(position == "ParameterName" ? new SqlParameter(new DbDataType(typeof(int)), name, 1) : One);

			if (position == "ColumnAlias")
				query.Select.Columns[0].Alias = name;

			return new SqlSelectStatement(query);
		}

		[Test]
		public void IdentifierOutsideShapeIsRejected([Values("TableSourceAlias", "TableAlias", "ColumnAlias", "ParameterName")] string position)
		{
			using var db = CreateConnection();

			Rejected(db, WithIdentifier(db, position, "not a name")).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.IdentifierNotMapped && r.Text == "not a name");
		}

		[Test]
		public void IdentifierInShapeIsAccepted([Values("TableSourceAlias", "TableAlias", "ColumnAlias", "ParameterName")] string position)
		{
			using var db = CreateConnection();

			RoundTrip(db, WithIdentifier(db, position, "a_name_1"), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		[Test]
		public void AllFieldSentinelIsAccepted()
		{
			using var db = CreateConnection();

			var table = MappedTable(db);
			var query = new SelectQuery();

			query.From.Tables.Add(new SqlTableSource(table, "t"));
			query.Select.Add(table.All);

			RoundTrip(db, new SqlSelectStatement(query), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		// every builder in the validator's argument table, with the identifier key it accepts, if any
		static readonly (string Builder, string? IdentifierKey)[] _builders =
		[
			("LinqToDB.Internal.SqlProvider.HintExtensionBuilder",                                 null),
			("LinqToDB.Internal.SqlProvider.HintWithParameterExtensionBuilder",                    "hintParameter"),
			("LinqToDB.Internal.SqlProvider.HintWithParametersExtensionBuilder",                   "hintParameters.0"),
			("LinqToDB.Internal.SqlProvider.HintWithFormatParametersExtensionBuilder",             null),
			("LinqToDB.Internal.DataProvider.TableSpecHintExtensionBuilder",                       "hintParameter"),
			("LinqToDB.DataProvider.ClickHouse.ClickHouseHints+TableHintExtensionBuilder",         null),
			("LinqToDB.DataProvider.Ydb.YdbHints+YdbQueryHintExtensionBuilder",                    null),
			("LinqToDB.DataProvider.PostgreSQL.PostgreSQLHints+SubQueryTableHintExtensionBuilder", null),
			("LinqToDB.DataProvider.MySql.MySqlHints+SubQueryTableHintExtensionBuilder",           null),
			("LinqToDB.DataProvider.SqlServer.SqlServerHints+WithForceSeekExtensionBuilder",       "indexName"),
			("LinqToDB.DataProvider.SqlServer.SqlServerHints+ParamsExtensionBuilder",              null),
			("LinqToDB.DataProvider.SqlServer.SqlServerHints+TableParamsExtensionBuilder",         null),
			("LinqToDB.DataProvider.SqlServer.SqlServerHints+TemporalTableExtensionBuilder",       null),
		];

		static IEnumerable<string> BuilderNames()           => _builders.Select(static b => b.Builder);
		static IEnumerable<string> BuildersWithIdentifier() => _builders.Where(static b => b.IdentifierKey != null).Select(static b => b.Builder);

		static SqlSelectStatement WithExtension(string builder, string key, ISqlExpression value)
		{
			var statement = SelectOf(One);

			statement.SelectQuery!.SqlQueryExtensions =
			[
				new SqlQueryExtension
				{
					Scope       = Sql.QueryExtensionScope.QueryHint,
					BuilderType = typeof(DataOptions).Assembly.GetType(builder, throwOnError: true),
					Arguments   = new(StringComparer.Ordinal) { [key] = value },
				},
			];

			return statement;
		}

		[Test]
		public void UnknownBuilderArgumentKeyIsRejected([ValueSource(nameof(BuilderNames))] string builder)
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension(builder, "unknown_key", One)).Rejections
				.ShouldContain(r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text == builder + ":unknown_key");
		}

		[Test]
		public void BuilderHintTextOutsideCatalogIsRejected([ValueSource(nameof(BuilderNames))] string builder)
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension(builder, "hint", new SqlValue(InertText))).Rejections
				.ShouldContain(static r => r.Kind == SqlTextKind.ExtensionArgument && r.Text == InertText);
		}

		[Test]
		public void BuilderIdentifierArgumentInShapeIsAccepted([ValueSource(nameof(BuildersWithIdentifier))] string builder)
		{
			var key = _builders.Single(b => b.Builder == builder).IdentifierKey!;

			using var db = CreateConnection();

			RoundTrip(db, WithExtension(builder, key, new SqlValue("IX_user_chosen_1")), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		[Test]
		public void NoneBuilderArgumentsAreNotChecked()
		{
			using var db = CreateConnection();

			// renders nothing, so even text outside the catalog never reaches the SQL
			RoundTrip(db, WithExtension("LinqToDB.SqlQuery.NoneExtensionBuilder", "hint", new SqlValue(InertText)), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		const string MappingAssemblyText = "MAPPING_ASSEMBLY_TEXT({0})";

		/// <summary>The catalog this test assembly contributes when a host lists it in <c>MappingAssemblies</c>.</summary>
		public sealed class MappingAssemblyCatalog : ISqlTextCatalog
		{
			public IReadOnlyList<SqlTextItem> Items { get; } = [new(SqlTextKind.Expression, MappingAssemblyText, null, nameof(RemoteQueryValidatorTests), false)];
		}

		[Test]
		public void MappingAssemblyCatalogContributesEntries()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlExpression(new DbDataType(typeof(int)), MappingAssemblyText, One));

			RoundTrip(db, statement, RemoteQueryPolicy.Create(db, [_thisAssembly], null));

			// without the assembly its catalog is not consulted: the text fails closed, and the exception names the provider
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, RemoteQueryPolicy.Create(db, [], null)))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
			exception.Provider.ShouldBe(db.DataProvider.Name);
		}

		[Test]
		public void UnregisteredStructIsRejectedUntilRegistered()
		{
			var schema = new MappingSchema();

			using (var db = CreateConnection(schema))
			{
				var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new UnregisteredStruct()));

				Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db, _thisAssembly)));
			}

			schema.AddScalarType(typeof(UnregisteredStruct));

			using (var db = CreateConnection(schema))
			{
				var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new UnregisteredStruct()));

				RoundTrip(db, statement, Untrusted(db, _thisAssembly));
			}
		}

		[Test]
		public void NameInUnknownAssemblyIsNotLoaded()
		{
			using var db = CreateConnection();

			var policy   = Untrusted(db);
			var resolves = 0;

			Assembly? OnResolve(object? sender, ResolveEventArgs args)
			{
				resolves++;
				return null;
			}

			AppDomain.CurrentDomain.AssemblyResolve += OnResolve;

			try
			{
				var resolved = policy.TryResolveName("Missing.Type, Tests.Not.Loaded.Assembly", out var type);

				resolved.ShouldBeFalse();
				type.ShouldBeNull();
				resolves.ShouldBe(0);
			}
			finally
			{
				AppDomain.CurrentDomain.AssemblyResolve -= OnResolve;
			}
		}

		[Test]
		public void AllViolationsAreReportedWithEscapedMessage()
		{
			using var db = CreateConnection();

			var statement = SelectOf(
				new SqlValue(new DbDataType(typeof(string)), new RecordingValue("a")),
				new SqlValue(new DbDataType(typeof(string)), new LocalEntity()));

			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			exception.Rejections.Count.ShouldBe(2);
			exception.Provider.ShouldBe(db.DataProvider.Name);
		}
	}
}
