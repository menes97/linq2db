using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using LinqToDB;
using LinqToDB.Common;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.DataProvider.SqlServer;
using LinqToDB.Internal.DataProvider;
using LinqToDB.Internal.Remote;
using LinqToDB.Internal.SqlProvider;
using LinqToDB.Internal.SqlQuery;
using LinqToDB.Mapping;
using LinqToDB.Remote;
using LinqToDB.SqlQuery;

using NUnit.Framework;

using Shouldly;

[assembly: SqlTextCatalog(typeof(Tests.AST.RemoteQueryValidatorTests.MappingAssemblyCatalog))]
[assembly: SqlTextCatalog(typeof(Tests.AST.RemoteQueryValidatorTests.SecondMappingAssemblyCatalog))]
[assembly: SqlTextCatalog(typeof(Tests.AST.RemoteQueryValidatorTests.BuilderRoles))]

namespace Tests.AST
{
	[TestFixture]
	public class RemoteQueryValidatorTests : TestBase
	{
		sealed class RecordingValue
		{
			// per test: tests run in parallel and the deserializer constructs instances on the test's flow
			static readonly AsyncLocal<StrongBox<int>?> _constructed = new();

			public static int Constructed => _constructed.Value?.Value ?? 0;

			public static void StartCounting() => _constructed.Value = new StrongBox<int>();

			readonly string _value;

			public RecordingValue(string value)
			{
				if (_constructed.Value is { } counter)
					Interlocked.Increment(ref counter.Value);

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

		// no [Table]: the hierarchy is stored in the root's table, named after the root
		[InheritanceMapping(Code = 1, Type = typeof(DerivedRow))]
		[InheritanceMapping(Code = 2, Type = typeof(OtherRow))]
		abstract class RootRow
		{
			[PrimaryKey]                       public int Id       { get; set; }
			[Column(IsDiscriminator = true)]   public int Code     { get; set; }
			[Column]                           public int ParentId { get; set; }
		}

		sealed class DerivedRow : RootRow
		{
			[Column] public string? Name { get; set; }
		}

		sealed class OtherRow : RootRow
		{
		}

		sealed class ParentRow
		{
			[PrimaryKey] public int Id { get; set; }

			[Association(ThisKey = nameof(Id), OtherKey = nameof(RootRow.ParentId))]
			public List<RootRow> Children { get; set; } = null!;
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

		// TO-15: what an untrusted client is refused, a trusted client (no policy) still sends
		static RemoteRequestRejectedException RejectedUnlessTrusted(DataConnection db, SqlStatement statement, RemoteQueryPolicy policy)
		{
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, policy))!;

			RoundTrip(db, statement, null);

			return exception;
		}

		static RemoteRejection RejectedSingle(DataConnection db, SqlStatement statement, SqlTextCatalog? catalog = null)
		{
			var policy    = RemoteQueryPolicy.Create(db, [], null, catalog);
			var exception = RejectedUnlessTrusted(db, statement, policy)!;

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

			rejection.Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
			rejection.Kind.ShouldBe(SqlTextKind.DbType);
		}

		const string MappedDbType = "MARKER TYPE(1 UNIT)";

		sealed class DbTypeEntity
		{
			[Column(DbType = MappedDbType)] public string? Value { get; set; }
		}

		[Test]
		public void DbTypeOfTheRequestTablesMappingIsAccepted()
		{
			using var db = CreateConnection();

			var table = new SqlTable(db.MappingSchema.GetEntityDescriptor(typeof(DbTypeEntity)));
			var value = new SqlValue(new DbDataType(typeof(string), DataType.NVarChar, MappedDbType), "MARKER");

			// control: without the mapped table in the request, the same type is neither mapped nor in the grammar
			Rejected(db, SelectOf(value)).Rejections.ShouldContain(static r => r.Kind == SqlTextKind.DbType && r.Text == MappedDbType);

			var query = new SelectQuery();

			query.From.Tables.Add(new SqlTableSource(table, "t"));
			query.Select.Add(value);

			RoundTrip(db, new SqlSelectStatement(query), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		[Test]
		public void CataloguedDbTypeIsAccepted()
		{
			using var db = CreateConnection();

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.DbType, MappedDbType);

			RoundTrip(db, SelectOf(new SqlValue(new DbDataType(typeof(string), DataType.NVarChar, MappedDbType), "MARKER")), RemoteQueryPolicy.Create(db, [], null, catalog));
		}

		[Test]
		public void DbTypeInShapeIsAccepted([Values("int", "varchar(10)", "decimal(18, 4)", "double precision", "nvarchar(max)", "varchar(MAX)", "text[]", "int [ ] [ ]")] string dbType)
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
		public void TagIsKept()
		{
			using var db = CreateConnection();

			var statement = SelectOf(One);

			var comment = new SqlComment();

			comment.Lines.Add("inert");

			typeof(SqlStatement).GetProperty(nameof(SqlStatement.Tag))!.SetValue(statement, comment);

			// BuildSqlComment removes every delimiter, so the text renders inert, as for a direct query
			var result = RoundTrip(db, statement, Untrusted(db));

			result.Statement.Tag!.Lines.ShouldBe(["inert"]);
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
			var exception = RejectedUnlessTrusted(db, statement, policy)!;

			exception.Rejections[0].NodeType.ShouldBe("ResultColumn");
		}

		[Test]
		public void ValueTypeOutsideAllowlistIsRejectedBeforeConversion()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(string)), new RecordingValue("x")));

			RecordingValue.StartCounting();

			// not RejectedUnlessTrusted: its trusted run constructs the value; ValueTypeIsConvertedWhenTrusted is the control
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			RecordingValue.Constructed.ShouldBe(0);
			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TypeNotAllowed);
		}

		[Test]
		public void ValueTypeInMappedAssemblyIsRejectedBeforeConversion()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(string)), new RecordingValue("x")));
			var policy    = Untrusted(db, _thisAssembly);

			// the name resolves, so the rejection below comes from the value role, not from an unknown assembly
			policy.TryResolveName(typeof(RecordingValue).AssemblyQualifiedName!, out var resolved).ShouldBeTrue();
			resolved.ShouldBe(typeof(RecordingValue));

			RecordingValue.StartCounting();

			// not RejectedUnlessTrusted: its trusted run constructs the value; ValueTypeIsConvertedWhenTrusted is the control
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, policy))!;

			RecordingValue.Constructed.ShouldBe(0);
			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TypeNotAllowed);
		}

		// a scalar value is converted while the request is read, before it is validated, so the XML converter must not process a DTD
		[Test]
		public void XmlDocumentTypeIsNotProcessed([Values] bool trusted)
		{
			using var db = CreateConnection();

			var document  = XDocument.Parse("<!DOCTYPE r [<!ENTITY e \"inert\">]><r>&e;</r>");
			var statement = SelectOf(new SqlValue(new DbDataType(typeof(XDocument), DataType.Xml), document));

			var exception = Assert.Catch(() => RoundTrip(db, statement, trusted ? null : Untrusted(db)))!;

			for (Exception? e = exception; ; e = e.InnerException)
			{
				e.ShouldNotBeNull(exception.ToString());

				if (e is XmlException)
					break;
			}
		}

		[Test]
		public void XmlDocumentIsAccepted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(XDocument), DataType.Xml), XDocument.Parse("<r>inert</r>")));
			var query     = RoundTrip(db, statement, Untrusted(db));

			var value = ((SqlSelectStatement)query.Statement).SelectQuery.Select.Columns[0].Expression.ShouldBeOfType<SqlValue>().Value;

			value.ShouldBeOfType<XDocument>().Root!.Value.ShouldBe("inert");
		}

		[Test]
		public void XmlElementIsRejected()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(XElement), DataType.Xml), new XElement("r", "inert")));

			// not RejectedUnlessTrusted: a trusted server cannot read an XElement value back either
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TypeNotAllowed);
		}

		[Test]
		public void DerivedTypeOfInheritanceHierarchyIsAccepted([Values] bool derived)
		{
			using var db = CreateConnection();

			var statement = derived
				? db.GetTable<RootRow>().OfType<DerivedRow>().GetStatement()
				: db.GetTable<RootRow>().GetStatement();

			RoundTrip(db, statement, Untrusted(db, _thisAssembly));
		}

		// an association to a hierarchy builds the derived type's table under the root's name
		[Test]
		public void DerivedTypeTableNamedAfterTheRootIsAccepted()
		{
			using var db = CreateConnection();

			var table = new SqlTable(db.MappingSchema.GetEntityDescriptor(typeof(DerivedRow))) { TableName = new SqlObjectName(nameof(RootRow)) };

			RoundTrip(db, SelectFrom(table), Untrusted(db, _thisAssembly));
		}

		[Test]
		public void DerivedTypeTableWithOtherNameIsRejected()
		{
			using var db = CreateConnection();

			var table = new SqlTable(db.MappingSchema.GetEntityDescriptor(typeof(DerivedRow))) { TableName = new SqlObjectName("NOT_A_MAPPED_NAME") };

			Rejected(db, SelectFrom(table)).Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "NOT_A_MAPPED_NAME");
		}

		[Test]
		public void FSharpOptionOfAllowedTypeResolves()
		{
			using var db = CreateConnection();

			var policy = Untrusted(db);

			policy.TryResolveName(typeof(Microsoft.FSharp.Core.FSharpOption<int>).AssemblyQualifiedName!, out var option).ShouldBeTrue();
			option.ShouldBe(typeof(Microsoft.FSharp.Core.FSharpOption<int>));

			policy.TryResolveName(typeof(Microsoft.FSharp.Core.FSharpValueOption<string>).AssemblyQualifiedName!, out _).ShouldBeTrue();

			// the serializer writes a generic type's full name, which qualifies only its arguments
			policy.TryResolveName(typeof(Microsoft.FSharp.Core.FSharpOption<int>).FullName!, out _).ShouldBeTrue();
		}

		[Test]
		public void FSharpOptionOfUnmappedTypeIsNotResolved()
		{
			using var db = CreateConnection();

			Untrusted(db).TryResolveName(typeof(Microsoft.FSharp.Core.FSharpOption<LocalEntity>).AssemblyQualifiedName!, out _).ShouldBeFalse();
		}

		[Test]
		public void OtherFSharpCoreTypeIsNotResolved()
		{
			using var db = CreateConnection();

			Untrusted(db).TryResolveName(typeof(Microsoft.FSharp.Collections.FSharpList<int>).AssemblyQualifiedName!, out _).ShouldBeFalse();
		}

		[Test]
		public void ValueTypeIsConvertedWhenTrusted()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(string)), new RecordingValue("x")));

			RecordingValue.StartCounting();

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

			var exception = RejectedUnlessTrusted(db, statement, Untrusted(db))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.BuilderNotAllowed);
		}

		[Test]
		public void TypeAllowedAsObjectIsRejectedAsValue()
		{
			using var db = CreateConnection();

			var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new LocalEntity()));

			// not RejectedUnlessTrusted: a trusted server cannot convert an entity value from its text either
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

			table.SqlTableType = SqlTableType.Expression;
			table.Expression   = TableExpression;

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
			table.SqlTableType = SqlTableType.Expression;
			table.Expression   = TableExpression;
			table.TableName    = new SqlObjectName("NOT_A_MAPPED_NAME");

			var exception = RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

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

			var exception = RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

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

			var exception = RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

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
			return RejectedUnlessTrusted(db, statement, RemoteQueryPolicy.Create(db, [_thisAssembly], null))!;
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
			var exception = RejectedUnlessTrusted(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)), text, One)), policy)!;

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

			var exception = RejectedUnlessTrusted(db, statement, RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

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

		// compiler-generated (VB closures, F# primary constructors) and non-ASCII parameter names are renamed, not rejected
		[TestCase("$VB$Local_id",  "VBLocal_id")]
		[TestCase("<Parameter>P",  "ParameterP")]
		[TestCase("параметр",      "p")]
		[TestCase("1 not a name",  "notaname")]
		public void ParameterNameOutsideShapeIsRenamed(string name, string expected)
		{
			using var db = CreateConnection();

			var query = RoundTrip(db, WithIdentifier(db, "ParameterName", name), RemoteQueryPolicy.Create(db, [_thisAssembly], null));

			((SqlSelectStatement)query.Statement).SelectQuery.Select.Columns[0].Expression.ShouldBeOfType<SqlParameter>().Name.ShouldBe(expected);
		}

		[Test]
		public void AliasOutsideShapeIsDropped([Values("TableSourceAlias", "TableAlias", "ColumnAlias")] string position, [Values("$VB$It", "not a name", "тбл")] string name)
		{
			using var db = CreateConnection();

			var query  = RoundTrip(db, WithIdentifier(db, position, name), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
			var select = ((SqlSelectStatement)query.Statement).SelectQuery;

			var alias = position switch
			{
				"TableSourceAlias" => select.From.Tables[0].Alias,
				"TableAlias"       => ((SqlTable)select.From.Tables[0].Source).Alias,
				_                  => select.Select.Columns[0].Alias,
			};

			alias.ShouldNotBe(name);
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

		static SqlSelectStatement WithExtension(string builder, string key, ISqlExpression value, Sql.QueryExtensionScope scope = Sql.QueryExtensionScope.QueryHint)
		{
			return WithExtension(typeof(DataOptions).Assembly.GetType(builder, throwOnError: true)!, key, value, scope);
		}

		static SqlSelectStatement WithExtension(Type builder, string key, ISqlExpression value, Sql.QueryExtensionScope scope = Sql.QueryExtensionScope.QueryHint)
		{
			var statement = SelectOf(One);

			statement.SelectQuery!.SqlQueryExtensions =
			[
				new SqlQueryExtension
				{
					Scope       = scope,
					BuilderType = builder,
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

		/// <summary>An extension builder of this test assembly, with roles declared by <see cref="BuilderRoles"/>.</summary>
		public sealed class MarkerHintBuilder : ISqlQueryExtensionBuilder
		{
			public void Build(NullabilityContext nullability, ISqlBuilder sqlBuilder, System.Text.StringBuilder stringBuilder, SqlQueryExtension sqlQueryExtension)
			{
				stringBuilder.Append("MARKER");
			}
		}

		/// <summary>Roles for this assembly's builder, and an attempt to widen a library builder's roles, which is ignored.</summary>
		public sealed class BuilderRoles : ISqlExtensionBuilderRoles
		{
			public IReadOnlyList<SqlExtensionArgument> Arguments { get; } =
			[
				new(typeof(MarkerHintBuilder),             "hint",    SqlExtensionArgumentRole.Text),
				new(typeof(MarkerHintBuilder),             "names.*", SqlExtensionArgumentRole.Identifier),
				new(typeof(TableSpecHintExtensionBuilder), "widened", SqlExtensionArgumentRole.Unrendered),
			];
		}

		[Test]
		public void DeclaredBuilderRolesAreApplied()
		{
			using var db = CreateConnection();

			var policy = RemoteQueryPolicy.Create(db, [_thisAssembly], null);

			RoundTrip(db, WithExtension(typeof(MarkerHintBuilder), "names.0", new SqlValue("IX_user_chosen_1")), policy);

			Rejected(db, WithExtension(typeof(MarkerHintBuilder), "names.0", new SqlValue("NOT A NAME"))).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.IdentifierNotMapped);

			Rejected(db, WithExtension(typeof(MarkerHintBuilder), "hint", new SqlValue(InertText))).Rejections
				.ShouldContain(static r => r.Kind == SqlTextKind.ExtensionArgument && r.Text == InertText);

			Rejected(db, WithExtension(typeof(MarkerHintBuilder), "undeclared", One)).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text.EndsWith(":undeclared", StringComparison.Ordinal));
		}

		[Test]
		public void BuilderRolesComeOnlyFromMappingAndProviderAssemblies()
		{
			using var db = CreateConnection();

			// the same assembly as a catalog-only source: its text is read, its builder roles and types are not
			var policy = RemoteQueryPolicy.Create(db, [], null, null, [_thisAssembly]);

			RejectedUnlessTrusted(db, WithExtension(typeof(MarkerHintBuilder), "names.0", new SqlValue("IX_user_chosen_1")), policy)!
				.Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.BuilderNotAllowed);
		}

		[Test]
		public void DeclaredRolesNeverWidenAnotherAssemblysBuilder()
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension(typeof(TableSpecHintExtensionBuilder), "widened", One)).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text.EndsWith(":widened", StringComparison.Ordinal));
		}

		[Test, NonParallelizable]
		public void BuilderNamedLikeALibraryBuilderGetsNoLibraryRoles()
		{
			using var db = CreateConnection();

			// same full name as the library's HintExtensionBuilder, declared in this assembly with no roles of its own;
			// only an assembly-qualified name on the wire reaches it, since a bare name resolves to the library first
			var saved = Configuration.LinqService.SerializeAssemblyQualifiedName;

			try
			{
				Configuration.LinqService.SerializeAssemblyQualifiedName = true;

				Rejected(db, WithExtension(typeof(LinqToDB.Internal.SqlProvider.HintExtensionBuilder), "hint", new SqlValue("MARKER"))).Rejections
					.ShouldContain(static r => r.Reason == RemoteRejectionReason.BuilderNotAllowed);
			}
			finally
			{
				Configuration.LinqService.SerializeAssemblyQualifiedName = saved;
			}
		}

		static Sql.SqlID MarkerID => new(Sql.SqlIDType.TableAlias, "MARKER");

		[Test]
		public void NonStringHintValueIsRejected([ValueSource(nameof(BuilderNames))] string builder)
		{
			using var db = CreateConnection();

			// a table ID renders as its type and text through ToString wherever a builder writes the hint as is
			Rejected(db, WithExtension(builder, "hint", new SqlValue(MarkerID))).Rejections
				.ShouldContain(r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text == builder + ":hint value");
		}

		[Test]
		public void HintTextFromAnotherElementIsRejected()
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension("LinqToDB.Internal.SqlProvider.HintExtensionBuilder", "hint", new SqlExpression(new DbDataType(typeof(string)), "{0}", new SqlValue("MARKER")))).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text.EndsWith(":hint value", StringComparison.Ordinal));
		}

		[Test]
		public void NumericIdentifierArgumentIsAccepted([ValueSource(nameof(BuildersWithIdentifier))] string builder)
		{
			var key = _builders.Single(b => b.Builder == builder).IdentifierKey!;

			using var db = CreateConnection();

			RoundTrip(db, WithExtension(builder, key, new SqlValue(10)), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		[Test]
		public void TableIdIsAcceptedOnlyWhereTheBuilderResolvesIt()
		{
			using var db = CreateConnection();

			RoundTrip(db, WithExtension("LinqToDB.Internal.SqlProvider.HintWithParameterExtensionBuilder", "hintParameter", new SqlValue(MarkerID)), RemoteQueryPolicy.Create(db, [_thisAssembly], null));

			// TableSpecHintExtensionBuilder writes its parameters as is, without BuildSqlID
			Rejected(db, WithExtension("LinqToDB.Internal.DataProvider.TableSpecHintExtensionBuilder", "hintParameter", new SqlValue(MarkerID))).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text.EndsWith(":hintParameter value", StringComparison.Ordinal));
		}

		[Test]
		public void CountArgumentMustBeAnInteger()
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension("LinqToDB.Internal.SqlProvider.HintWithParametersExtensionBuilder", "hintParameters.Count", new SqlValue("1"))).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text.EndsWith(":hintParameters.Count value", StringComparison.Ordinal));
		}

		[Test]
		public void IdentifierWithTrailingLineFeedIsRejected()
		{
			using var db = CreateConnection();

			Rejected(db, WithExtension("LinqToDB.Internal.SqlProvider.HintWithParameterExtensionBuilder", "hintParameter", new SqlValue("IX_user_chosen_1\n"))).Rejections
				.ShouldContain(static r => r.Reason == RemoteRejectionReason.IdentifierNotMapped);
		}

		static DataConnection CreateSqlServerConnection()
		{
			// never opened: a request is only deserialized and validated
			return new DataConnection(new DataOptions().UseSqlServer("Server=localhost", SqlServerVersion.v2022, SqlServerProvider.MicrosoftDataSqlClient));
		}

		static SqlSelectStatement WithJoinHint(string hint)
		{
			return WithExtension(NoneBuilderName, "hint", new SqlValue(hint), Sql.QueryExtensionScope.JoinHint);
		}

		const string NoneBuilderName = "LinqToDB.SqlQuery.NoneExtensionBuilder";

		[Test]
		public void JoinHintTextIsCheckedWhereTheSqlBuilderRendersIt()
		{
			using (var db = CreateSqlServerConnection())
			{
				RejectedUnlessTrusted(db, WithJoinHint(InertText), RemoteQueryPolicy.Create(db, [], null))!
					.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.ExtensionArgument && r.Text == InertText);

				// control: the library's own join hint is catalogued
				RoundTrip(db, WithJoinHint(SqlServerHints.Join.Loop), RemoteQueryPolicy.Create(db, [], null));
			}

			// the SQLite builder never renders a join hint, so its text stays unchecked
			using (var db = CreateConnection())
				RoundTrip(db, WithJoinHint(InertText), RemoteQueryPolicy.Create(db, [], null));
		}

		[Test]
		public void TableTypeWithExpressionIsRejected()
		{
			using var db = CreateConnection();

			const string expression = "NOT_THE_NAME({2}) {1}";

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, expression);

			var table = MappedTable(db);

			// the builder renders the name of a plain table and ignores the expression, so the pair is never library output
			table.Expression     = expression;
			table.TableName      = new SqlObjectName("NOT_A_MAPPED_NAME");
			table.TableArguments = [One];

			var exception = RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

			exception.Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text == "SqlTable.Expression");
			exception.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "NOT_A_MAPPED_NAME");
		}

		[Test]
		public void NameOfExpressionTableWithoutNamePlaceholderMustBeIdentifierShaped()
		{
			using var db = CreateConnection();

			const string expression = "NOT_THE_NAME({2}) {1}";

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableExpression, expression);

			var table = MappedTable(db);

			// the name still qualifies fields that have no table alias
			table.SqlTableType   = SqlTableType.Expression;
			table.Expression     = expression;
			table.TableName      = new SqlObjectName("NOT A NAME");
			table.TableArguments = [One];

			var exception = RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog))!;

			exception.Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.IdentifierNotMapped && r.Text == "NOT A NAME");
		}

		static SqlTable FunctionTable(DataConnection db, string expression, string name)
		{
			var table = MappedTable(db);

			table.SqlTableType = SqlTableType.Function;
			table.Expression   = expression;
			table.TableName    = new SqlObjectName(name);

			return table;
		}

		[Test]
		public void TableFunctionExpressionMayOnlyRepeatItsName()
		{
			using var db = CreateConnection();

			RejectedUnlessTrusted(db, SelectFrom(FunctionTable(db, "MARKER_OTHER", "MARKER_FUNCTION")), RemoteQueryPolicy.Create(db, [_thisAssembly], null))!
				.Rejections.ShouldContain(static r => r.Reason == RemoteRejectionReason.NodeNotAllowed && r.Text == "SqlTable.Expression");
		}

		[Test]
		public void TableFunctionNameIsCheckedAsTableName()
		{
			using var db = CreateConnection();

			var table = FunctionTable(db, "MARKER_FUNCTION", "MARKER_FUNCTION");

			RejectedUnlessTrusted(db, SelectFrom(table), RemoteQueryPolicy.Create(db, [_thisAssembly], null))!
				.Rejections.ShouldContain(static r => r.Kind == SqlTextKind.TableName && r.Text == "MARKER_FUNCTION");

			var catalog = new SqlTextCatalog();

			catalog.Add(SqlTextKind.TableName, "MARKER_FUNCTION");

			RoundTrip(db, SelectFrom(FunctionTable(db, "MARKER_FUNCTION", "MARKER_FUNCTION")), RemoteQueryPolicy.Create(db, [_thisAssembly], null, catalog));
		}

		/// <summary>A test-owned type a reader could build from a database string; records whether it ever was.</summary>
		public sealed class ProbeResult
		{
			public static int Constructed;

			public ProbeResult(string value)
			{
				Constructed++;
				Value = value;
			}

			public string Value { get; }
		}

		sealed class SQLiteService : LinqService
		{
			public override DataConnection CreateDataContext(string? configuration) => CreateConnection();
		}

		// Characterization, not red-to-green: on this path the reader is built for the column type that was validated (the int
		// of the first branch), so the folded branch's type never reaches it, and LinqService's check at the reader is a second
		// line behind validation. The assertion that matters is that the folded branch's type is never constructed.
		[Test, NonParallelizable]
		public void FoldedResultColumnNeverBuildsTheFoldedBranchType()
		{
			var service = new SQLiteService();

			service.MappingAssemblies.Add(_thisAssembly);

			var column = new SqlConditionExpression(SqlPredicate.False, new SqlValue(1), new SqlValue(new DbDataType(typeof(ProbeResult)), "MARKER"));

			using var db = CreateConnection();

			var (serialize, _, schema) = GetSerializer();
			var data                   = (string)serialize.Invoke(null, [schema, SelectOf(column), null, null, db.Options])!;
			var constructed            = ProbeResult.Constructed;

			Assert.CatchAsync<LinqToDBException>(async () => await service.ExecuteReaderAsync(null, data));

			ProbeResult.Constructed.ShouldBe(constructed);
		}

		sealed class SchemaService(MappingSchema? schema) : LinqService
		{
			public MappingSchema? Schema { get; set; } = schema;

			public override DataConnection CreateDataContext(string? configuration) => CreateConnection(Schema);
		}

		static string Serialize(SqlStatement statement)
		{
			using var db = CreateConnection();

			var (serialize, _, schema) = GetSerializer();

			return (string)serialize.Invoke(null, [schema, statement, null, null, db.Options])!;
		}

		// the request is either rejected by the policy, or accepted and then fails in SQLite, which does not matter here
		static async Task<bool> IsRejected(LinqService service, string data)
		{
			try
			{
				await service.ExecuteScalarAsync(null, data);
				return false;
			}
			catch (RemoteRequestRejectedException)
			{
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		[Test]
		public async Task MappingAssemblyAddedAfterARequestTakesEffect()
		{
			var service = new SchemaService(null);
			var data    = Serialize(SelectOf(new SqlExpression(new DbDataType(typeof(int)), MappingAssemblyText, One)));

			(await IsRejected(service, data)).ShouldBeTrue();

			service.MappingAssemblies.Add(_thisAssembly);

			(await IsRejected(service, data)).ShouldBeFalse();

			service.MappingAssemblies.Remove(_thisAssembly);

			(await IsRejected(service, data)).ShouldBeTrue();
		}

		[Test]
		public async Task ScalarTypeRegisteredAfterARequestTakesEffect()
		{
			var schema  = new MappingSchema();
			var service = new SchemaService(schema);
			var data    = Serialize(SelectOf(new SqlValue(new DbDataType(typeof(int)), new UnregisteredStruct())));

			service.MappingAssemblies.Add(_thisAssembly);

			(await IsRejected(service, data)).ShouldBeTrue();

			schema.AddScalarType(typeof(UnregisteredStruct));

			(await IsRejected(service, data)).ShouldBeFalse();
		}

		[Test]
		public async Task SharedPolicyCacheIsBounded()
		{
			var service = new SchemaService(null);
			var data    = Serialize(SelectOf(One));
			var count   = typeof(LinqService).GetProperty("SharedPolicyCount", BindingFlags.Instance | BindingFlags.NonPublic)!;

			// a schema per request, as some hosts build them
			for (var i = 0; i < 100; i++)
			{
				var schema = new MappingSchema();

				schema.SetConvertExpression<int, MarkerScalar>(v => new MarkerScalar());
				service.Schema = schema;

				await IsRejected(service, data);
			}

			((int)count.GetValue(service)!).ShouldBeLessThanOrEqualTo(64);
		}

		sealed class MarkerScalar;

		static string NestedListName(int depth) => string.Concat(Enumerable.Repeat("System.Collections.Generic.List`1[[", depth)) + "System.Int32" + string.Concat(Enumerable.Repeat("]]", depth));

		[Test]
		public void DeeplyNestedTypeNameIsNotResolved()
		{
			using var db = CreateConnection();

			var policy = RemoteQueryPolicy.Create(db, [], null);

			// control: the same shape, shallower, resolves
			policy.TryResolveName(NestedListName(2), out _).ShouldBeTrue();

			policy.TryResolveName(NestedListName(9), out _).ShouldBeFalse();
		}

		[Test, NonParallelizable]
		public void OverlongTypeNameNeverReachesTypeResolver()
		{
			using var db = CreateConnection();

			var seen  = new List<string>();
			var saved = LinqService.TypeResolver;

			try
			{
				LinqService.TypeResolver = name =>
				{
					lock (seen)
						seen.Add(name);
					return null;
				};

				var policy = RemoteQueryPolicy.Create(db, [], null);

				// control: an unresolved name of ordinary length is offered to the host's resolver
				policy.TryResolveName("MARKER_TYPE", out _).ShouldBeFalse();
				policy.TryResolveName(new string('A', 2000), out _).ShouldBeFalse();
			}
			finally
			{
				LinqService.TypeResolver = saved;
			}

			seen.ShouldBe(["MARKER_TYPE"]);
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
			var exception = RejectedUnlessTrusted(db, statement, RemoteQueryPolicy.Create(db, [], null))!;

			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.TextNotInCatalog);
			exception.Provider.ShouldBe(db.DataProvider.Name);
		}

		const string SecondMappingAssemblyText = "SECOND_MAPPING_ASSEMBLY_TEXT({0})";

		/// <summary>A second catalog of this test assembly, as a generated catalog beside a hand-written one would be.</summary>
		public sealed class SecondMappingAssemblyCatalog : ISqlTextCatalog
		{
			public IReadOnlyList<SqlTextItem> Items { get; } = [new(SqlTextKind.Expression, SecondMappingAssemblyText, null, nameof(RemoteQueryValidatorTests), false)];
		}

		[Test]
		public void EveryCatalogOfAnAssemblyContributesEntries()
		{
			using var db = CreateConnection();

			var policy = RemoteQueryPolicy.Create(db, [_thisAssembly], null);

			RoundTrip(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)), MappingAssemblyText,       One)), policy);
			RoundTrip(db, SelectOf(new SqlExpression(new DbDataType(typeof(int)), SecondMappingAssemblyText, One)), RemoteQueryPolicy.Create(db, [_thisAssembly], null));
		}

		// Every text-bearing member of every query element, with how an untrusted request's value is handled.
		// A member added later is not in this list and fails the guard below until someone records its disposition.
		static readonly string[] _textMemberDispositions =
		[
			"LinqToDB.Internal.SqlQuery.CteClause.Name: identifier shape",
			"LinqToDB.Internal.SqlQuery.QueryElement.DebugText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SelectQuery.QueryName: identifier shape",
			"LinqToDB.Internal.SqlQuery.SelectQuery.SqlText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlBinaryExpression.Operation: closed operator set, else Operator catalog text",
			"LinqToDB.Internal.SqlQuery.SqlBinaryExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlCaseExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlCaseExpression._dataType: backs Type",
			"LinqToDB.Internal.SqlQuery.SqlCastExpression.ToType: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlCastExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlColumn.Alias: identifier shape, else dropped; * admitted",
			"LinqToDB.Internal.SqlQuery.SqlColumn.RawAlias: identifier shape, else dropped; * admitted",
			"LinqToDB.Internal.SqlQuery.SqlComment.Lines: only SqlStatement.Tag holds a comment; BuildSqlComment removes every delimiter",
			"LinqToDB.Internal.SqlQuery.SqlCreateTableStatement.StatementFooter: StatementFooter catalog text",
			"LinqToDB.Internal.SqlQuery.SqlCreateTableStatement.StatementHeader: StatementHeader catalog text",
			"LinqToDB.Internal.SqlQuery.SqlCteField.Name: identifier shape",
			"LinqToDB.Internal.SqlQuery.SqlCteField.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlCteTable.Alias: identifier shape",
			"LinqToDB.Internal.SqlQuery.SqlCteTable.SqlText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlCteTable.TableName: derived from the checked CteClause, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlCteTableField.Name: derived from the checked SqlCteField, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlCteTableField.Type: derived from the checked SqlCteField, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlDefaultValueExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlExpression.Expr: Expression catalog text",
			"LinqToDB.Internal.SqlQuery.SqlExtendedFunction.FunctionName: Function catalog text",
			"LinqToDB.Internal.SqlQuery.SqlExtendedFunction.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlField.CreateFormat: rejected",
			"LinqToDB.Internal.SqlQuery.SqlField.PhysicalName: mapped name for a mapped table, else identifier shape",
			"LinqToDB.Internal.SqlQuery.SqlFieldBase.Name: on the wire for SqlField but never rendered, PhysicalName is",
			"LinqToDB.Internal.SqlQuery.SqlFieldBase.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlFragment.Expr: Fragment catalog text",
			"LinqToDB.Internal.SqlQuery.SqlFunction.Name: Function catalog text",
			"LinqToDB.Internal.SqlQuery.SqlIntervalDifferenceExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlIntervalExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlIntervalPartExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlMergeStatement.Hint: MergeHint catalog text",
			"LinqToDB.Internal.SqlQuery.SqlObjectNameExpression.Name: element not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlParameter.Name: identifier shape, else renamed to it",
			"LinqToDB.Internal.SqlQuery.SqlParameter.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlParameterizedExpressionBase.ExprOrName: base of SqlExpression.Expr and SqlFunction.Name",
			"LinqToDB.Internal.SqlQuery.SqlParameterizedExpressionBase.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlPredicate+Like.FunctionName: Function catalog text",
			"LinqToDB.Internal.SqlQuery.SqlQueryExtension.Arguments: per-builder argument roles",
			"LinqToDB.Internal.SqlQuery.SqlQueryExtension.Configuration: matched against the provider, never rendered",
			"LinqToDB.Internal.SqlQuery.SqlRawSqlTable.SQL: RawSqlTable catalog text",
			"LinqToDB.Internal.SqlQuery.SqlRawSqlTable.SqlText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlStatement.DebugSqlText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlStatement.SqlText: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlTable.Alias: identifier shape, else dropped",
			"LinqToDB.Internal.SqlQuery.SqlTable.Expression: TableExpression catalog text",
			"LinqToDB.Internal.SqlQuery.SqlTable.ID: matched by table hints, never rendered",
			"LinqToDB.Internal.SqlQuery.SqlTable.NameForLogging: computed, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlTable.TableName: each part differing from the mapping is TableName catalog text",
			"LinqToDB.Internal.SqlQuery.SqlTable._fieldsLookup: lookup over the checked fields",
			"LinqToDB.Internal.SqlQuery.SqlTableLikeSource.Name: derived, not on the wire",
			"LinqToDB.Internal.SqlQuery.SqlTableSource.Alias: identifier shape, else dropped",
			"LinqToDB.Internal.SqlQuery.SqlTableSource.RawAlias: identifier shape, else dropped; $ and $F admitted",
			"LinqToDB.Internal.SqlQuery.SqlTableSource._alias: backs Alias",
			"LinqToDB.Internal.SqlQuery.SqlTemporalArithmeticExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlUnaryExpression.Type: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.Internal.SqlQuery.SqlValue.ValueType: DbDataType, DbType shape in ReadDbDataType",
			"LinqToDB.SqlQuery.SqlDataType.Type: DbDataType, DbType shape in ReadDbDataType",
		];

		static bool IsTextBearing(Type type)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;

			if (type == typeof(string) || type == typeof(DbDataType) || type == typeof(SqlObjectName))
				return true;

			return type.GetInterfaces().Concat([type]).Any(static i => i.IsGenericType
				&& (i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && i.GetGenericArguments()[0] == typeof(string)
				 || i.GetGenericTypeDefinition() == typeof(IDictionary<,>) && i.GetGenericArguments()[0] == typeof(string)
				 || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) && i.GetGenericArguments()[0] == typeof(string)));
		}

		[Test]
		public void EveryTextBearingMemberHasADisposition()
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

			var actual = typeof(DataOptions).Assembly.GetTypes()
				.Where(static t => typeof(IQueryElement).IsAssignableFrom(t) && !t.IsInterface)
				.SelectMany(t => t.GetFields(flags).Where(static f => f.Name[0] != '<').Select(static f => (Member: (MemberInfo)f, Type: f.FieldType))
					.Concat(t.GetProperties(flags).Select(static p => (Member: (MemberInfo)p, Type: p.PropertyType)))
					.Where(m => IsTextBearing(m.Type))
					.Select(m => $"{t.FullName}.{m.Member.Name}"))
				.Distinct()
				.OrderBy(static n => n, StringComparer.Ordinal)
				.ToList();

			var recorded = _textMemberDispositions.Select(static d => d.Split(':')[0]).ToList();

			actual.Except(recorded).ShouldBeEmpty("text-bearing members without a disposition");
			recorded.Except(actual).ShouldBeEmpty("dispositions for members that no longer exist");
		}

		[Test]
		public void UnregisteredStructIsRejectedUntilRegistered()
		{
			var schema = new MappingSchema();

			using (var db = CreateConnection(schema))
			{
				var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new UnregisteredStruct()));

				RejectedUnlessTrusted(db, statement, Untrusted(db, _thisAssembly));
			}

			schema.AddScalarType(typeof(UnregisteredStruct));

			using (var db = CreateConnection(schema))
			{
				var statement = SelectOf(new SqlValue(new DbDataType(typeof(int)), new UnregisteredStruct()));

				RoundTrip(db, statement, Untrusted(db, _thisAssembly));
			}
		}

		// NonParallelizable: AssemblyResolve is process-global, so another test's resolution would be counted
		[Test, NonParallelizable]
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

			// not RejectedUnlessTrusted: a trusted server cannot convert an entity value from its text either
			var exception = Assert.Throws<RemoteRequestRejectedException>(() => RoundTrip(db, statement, Untrusted(db)))!;

			exception.Rejections.Count.ShouldBe(2);
			exception.Provider.ShouldBe(db.DataProvider.Name);
		}
	}
}
