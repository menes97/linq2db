using System;
using System.Collections.Generic;
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
