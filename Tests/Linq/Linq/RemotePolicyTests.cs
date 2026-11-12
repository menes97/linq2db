using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Remote;

using NUnit.Framework;

using Shouldly;

namespace Tests.Linq
{
	[TestFixture]
	public class RemotePolicyTests : TestBase
	{
		sealed class CountingService : LinqService
		{
			public int Created;

			public override DataConnection CreateDataContext(string? configuration)
			{
				Created++;
				throw new InvalidOperationException("created");
			}
		}

		static Task Call(CountingService service, string entryPoint, string? configuration)
		{
			return entryPoint switch
			{
				"GetInfo"    => service.GetInfoAsync(configuration),
				"NonQuery"   => service.ExecuteNonQueryAsync(configuration, ""),
				"Scalar"     => service.ExecuteScalarAsync(configuration, ""),
				"Reader"     => service.ExecuteReaderAsync(configuration, ""),
				"Batch"      => service.ExecuteBatchAsync(configuration, ""),
				_            => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
			};
		}

		const string Blocked = "Blocked.Configuration";

		[Test]
		public void DisallowedConfigurationIsRejectedBeforeCreatingContext([Values("GetInfo", "NonQuery", "Scalar", "Reader", "Batch")] string entryPoint)
		{
			var service = new CountingService();

			var exception = Assert.ThrowsAsync<RemoteRequestRejectedException>(async () => await Call(service, entryPoint, Blocked))!;

			service.Created.ShouldBe(0);
			exception.Rejections[0].Reason.ShouldBe(RemoteRejectionReason.ConfigurationNotAllowed);
		}

		[Test]
		public void DefaultConfigurationIsAcceptedWhenListIsEmpty()
		{
			var service = new CountingService();

			Assert.ThrowsAsync<InvalidOperationException>(async () => await Call(service, "Reader", null));

			service.Created.ShouldBe(1);
		}

		[Test]
		public void DefaultConfigurationIsRejectedWhenListOmitsIt()
		{
			var service = new CountingService();

			service.AllowedConfigurations.Add("Allowed");

			Assert.ThrowsAsync<RemoteRequestRejectedException>(async () => await Call(service, "Reader", null));

			service.Created.ShouldBe(0);
		}

		[Test]
		public void ListedConfigurationIsAccepted()
		{
			var service = new CountingService();

			service.AllowedConfigurations.Add("Allowed");

			Assert.ThrowsAsync<InvalidOperationException>(async () => await Call(service, "Reader", "Allowed"));

			service.Created.ShouldBe(1);
		}

		[Test]
		public void ConfigurationNameIsEscapedInMessage()
		{
			var service = new CountingService();

			var exception = Assert.ThrowsAsync<RemoteRequestRejectedException>(async () => await Call(service, "Reader", "MARKER\r\nFORGED LINE"))!;

			// the message has a line per rejection; none may start with text the client chose
			exception.Message.Split('\n').ShouldNotContain(static line => line.TrimStart().StartsWith("FORGED", StringComparison.Ordinal));
			exception.Message.ShouldContain("\\u000d\\u000aFORGED LINE");
		}

		sealed class GetInfoOverride : LinqService
		{
			public readonly List<Exception> Handled = new();

			// an override that never calls base, so never runs the check in the base method
			public override Task<LinqServiceInfo> GetInfoAsync(string? configuration, CancellationToken cancellationToken = default)
			{
				return Task.FromResult(new LinqServiceInfo());
			}

			protected override void HandleException(Exception exception)
			{
				Handled.Add(exception);
			}
		}

		[Test]
		public void GetInfoGateHoldsForAnOverrideThatSkipsBase()
		{
			var service = new GetInfoOverride();

			// control: the override itself runs no check
			Assert.DoesNotThrowAsync(async () => await service.GetInfoAsync(Blocked));

			Assert.ThrowsAsync<RemoteRequestRejectedException>(async () => await ((ILinqService)service).GetInfoAsync(Blocked));

			service.Handled.ShouldHaveSingleItem().ShouldBeOfType<RemoteRequestRejectedException>();
		}

		[Test]
		public void OversizedElementCountIsRejectedBeforeAllocation([Values(true, false)] bool trusted)
		{
			var service = new SQLiteService { TrustedClient = trusted };

			// the batch path reads a count of request strings before anything else; the count is bounded by the request length
			var exception = Assert.CatchAsync<LinqToDBException>(async () => await service.ExecuteBatchAsync(null, " 2000000000 "))!;

			exception.Message.ShouldContain("element count");
		}

		sealed class ConnectionFactory : IDataContextFactory<DataConnection>
		{
			public DataConnection CreateDataContext(string? configuration) => throw new InvalidOperationException();
		}

		[Test]
		public void GenericServiceDoesNotRegisterTheLibraryAssembly()
		{
			var service = new LinqService<DataConnection>(new ConnectionFactory());

			service.MappingAssemblies.ShouldNotContain(typeof(DataConnection).Assembly);
		}

		[Test]
		public void TrustedClientIgnoresList()
		{
			var service = new CountingService { TrustedClient = true };

			Assert.ThrowsAsync<InvalidOperationException>(async () => await Call(service, "Reader", Blocked));

			service.Created.ShouldBe(1);
		}

		sealed class SQLiteService : LinqService
		{
			public override DataConnection CreateDataContext(string? configuration)
			{
				return new DataConnection(new DataOptions().UseSQLite("Data Source=:memory:", SQLiteProvider.Microsoft));
			}
		}

		const string HostText    = "HOST_TEXT({})";
		const string NoMatchText = "MATCHES_NOTHING({})";

		static SqlTextEntry LibraryEntry(LinqService service)
		{
			return service.DumpSqlText().First(static e => e.Source == "linq2db" && e.Origin != null && e.State == SqlTextEntryState.Allowed);
		}

		static HashSet<(SqlTextKind, string)> Allowed(IEnumerable<SqlTextEntry> entries)
		{
			return [.. entries.Where(static e => e.State == SqlTextEntryState.Allowed).Select(static e => (e.Kind, e.Text))];
		}

		[Test]
		public void DumpListsSourceOriginAndRemovalState()
		{
			var service = new SQLiteService();
			var library = LibraryEntry(service);

			service.SqlText.Remove(library.Kind, library.Text);
			service.SqlText.Add(SqlTextKind.Expression, HostText);

			var dump = service.DumpSqlText();

			var removed = dump.Single(e => e.Kind == library.Kind && e.Text == library.Text);
			removed.State.ShouldBe(SqlTextEntryState.Removed);
			removed.Source.ShouldBe("linq2db");
			removed.Origin.ShouldBe(library.Origin);

			var host = dump.Single(static e => e.Text == HostText);
			host.Source.ShouldBe(SqlTextCatalog.HostSource);
			host.State.ShouldBe(SqlTextEntryState.Allowed);
		}

		[Test]
		public void ExportThenLoadYieldsEqualEffectiveSet()
		{
			var service = new SQLiteService();
			var library = LibraryEntry(service);

			service.SqlText.Remove(library.Kind, library.Text);
			service.SqlText.Add(SqlTextKind.QueryHint, "-- host\thint\nwith \\ escapes");

			using var writer = new StringWriter();

			SqlTextCatalog.Export(writer, service.DumpSqlText());

			var copy = new SQLiteService();

			// control: without the load, the copy differs, so the comparison below can fail
			Allowed(copy.DumpSqlText()).SetEquals(Allowed(service.DumpSqlText())).ShouldBeFalse();

			copy.SqlText.Load(new StringReader(writer.ToString()));

			Allowed(copy.DumpSqlText()).SetEquals(Allowed(service.DumpSqlText())).ShouldBeTrue();
		}

		[Test]
		public void DefaultExportOmitsLibraryEntries()
		{
			var service = new SQLiteService();
			var library = LibraryEntry(service);

			service.SqlText.Add(SqlTextKind.Expression, HostText);

			using var host = new StringWriter();
			using var full = new StringWriter();

			service.SqlText.Export(host);
			SqlTextCatalog.Export(full, service.DumpSqlText());

			host.ToString().ShouldContain(HostText);
			host.ToString().ShouldNotContain(library.Text);
			full.ToString().ShouldContain(library.Text);
		}

		[Test]
		public void LoadRejectsMalformedLineAndAppliesNothing()
		{
			var service = new SQLiteService();

			Assert.Throws<FormatException>(() => service.SqlText.Load(new StringReader($"+\tExpression\t{HostText}\n+\tNotAKind\tX")));

			service.DumpSqlText().ShouldNotContain(static e => e.Text == HostText);
		}

		// NonParallelizable: swaps the process-global DataConnection.WriteTraceLine
		[Test, NonParallelizable]
		public void StaleRemovalIsReportedInDumpAndTrace()
		{
			var service = new SQLiteService();
			var library = LibraryEntry(service);

			service.SqlText.Remove(SqlTextKind.Expression, NoMatchText);
			service.SqlText.Remove(library.Kind, library.Text);

			var dump = service.DumpSqlText();

			dump.Single(static e => e.Text == NoMatchText).State.ShouldBe(SqlTextEntryState.StaleRemoval);
			dump.Single(e => e.Kind == library.Kind && e.Text == library.Text).State.ShouldBe(SqlTextEntryState.Removed);

			var messages = new List<string>();
			var saved    = DataConnection.WriteTraceLine;

			try
			{
				DataConnection.WriteTraceLine = (message, category, _) =>
				{
					if (category == nameof(LinqService))
						lock (messages)
							messages.Add(message);
				};

				// the policy is created before the empty request fails to parse; it reports once per catalog version
				Assert.CatchAsync(async () => await service.ExecuteScalarAsync(null, ""));
				Assert.CatchAsync(async () => await service.ExecuteScalarAsync(null, ""));
			}
			finally
			{
				DataConnection.WriteTraceLine = saved;
			}

			messages.Count.ShouldBe(1);
			messages[0].ShouldContain(NoMatchText);
			messages[0].ShouldNotContain(library.Text);
		}

		sealed class PostgreSQLService : LinqService
		{
			// never opened: the catalog needs only the provider and its mapping schema
			public override DataConnection CreateDataContext(string? configuration)
			{
				return new DataConnection(new DataOptions().UsePostgreSQL("Server=localhost", PostgreSQLVersion.v15));
			}
		}

		[TestCase("MARKER('{0}')",          Description = "apostrophe")]
		[TestCase("MARKER('x{name}')",      Description = "named token")]
		[TestCase("MARKER('{expr?}')",      Description = "optional token")]
		[TestCase("MARKER('a''b{0}')",      Description = "after a doubled quote")]
		[TestCase("MARKER('a\\' {0} ')",    Description = "backslash reading")]
		[TestCase("MARKER(\"{0}\")",        Description = "double quote")]
		[TestCase("MARKER({0}) -- {1}",     Description = "line comment")]
		[TestCase("MARKER({0}) /* {1} */",  Description = "block comment")]
		[TestCase("MARKER('{}')",           Description = "exported placeholder spelling")]
		[TestCase("MARKER('open",           Description = "unterminated quote")]
		[TestCase("MARKER({0}) /* open",    Description = "unterminated block comment")]
		[TestCase("MARKER({0}) --",         Description = "line comment to the end")]
		public void AddRefusesFormatItemInQuotedContext(string text)
		{
			var service = new SQLiteService();

			Assert.Throws<ArgumentException>(() => service.SqlText.Add(SqlTextKind.Expression, text));

			service.DumpSqlText().ShouldNotContain(static e => e.Source == SqlTextCatalog.HostSource);
		}

		[TestCase("MARKER('',{0},' ')",              Description = "quotes beside format items")]
		[TestCase("MARKER('it''s', {0})",            Description = "doubled quote")]
		[TestCase("MARKER('{{0}}', {0})",            Description = "escaped braces")]
		[TestCase("MARKER({array, ', '})",           Description = "delimiter quoted inside a token")]
		[TestCase("MARKER({0}) -- note\nMORE({1})",  Description = "terminated line comment")]
		[TestCase("($t) -> {{ return $t.{0} }}",     Description = "dollar variable")]
		[TestCase("$Length$",                        Description = "dollar name without a closer")]
		public void AddAcceptsQuotedTextWithoutFormatItem(string text)
		{
			var service = new SQLiteService();

			service.SqlText.Add(SqlTextKind.Expression, text);

			service.DumpSqlText().ShouldContain(static e => e.Source == SqlTextCatalog.HostSource && e.State == SqlTextEntryState.Allowed);
		}

		[Test]
		public void LineCommentQueryHintIsAccepted()
		{
			var service = new SQLiteService();

			// a query hint is written on a line of its own, so its comment ends there
			service.SqlText.Add(SqlTextKind.QueryHint, "-- MARKER comment");

			Assert.Throws<ArgumentException>(() => service.SqlText.Add(SqlTextKind.QueryHint, "-- MARKER {0}"));

			// the same text in a position written inline still leaves the comment open
			Assert.Throws<ArgumentException>(() => service.SqlText.Add(SqlTextKind.Expression, "-- MARKER comment"));

			service.DumpSqlText().ShouldContain(static e => e.Kind == SqlTextKind.QueryHint && e.State == SqlTextEntryState.Allowed);
		}

		[Test]
		public void LoadRefusesQuotedFormatItemAndAppliesNothing()
		{
			var service = new SQLiteService();

			Assert.Throws<FormatException>(() => service.SqlText.Load(new StringReader($"+\tExpression\t{HostText}\n+\tExpression\tMARKER('{{0}}')")));

			service.DumpSqlText().ShouldNotContain(static e => e.Text == HostText);
		}

		[Test]
		public void LoadAcceptsQuotedFormatItemAsRemoval()
		{
			var service = new SQLiteService();

			service.SqlText.Load(new StringReader("-\tExpression\tMARKER('{}')"));

			service.DumpSqlText().Single(static e => e.Source == SqlTextCatalog.HostSource).State.ShouldBe(SqlTextEntryState.StaleRemoval);
		}

		const string BracketText = "MARKER_ARRAY[{0}]";

		[Test]
		public void BracketedFormatItemIsRefusedOnlyWhereBracketsQuote()
		{
			var sqlite     = new SQLiteService();
			var postgreSQL = new PostgreSQLService();

			sqlite    .SqlText.Add(SqlTextKind.Expression, BracketText);
			postgreSQL.SqlText.Add(SqlTextKind.Expression, BracketText);

			sqlite    .DumpSqlText().Single(static e => e.Source == SqlTextCatalog.HostSource).State.ShouldBe(SqlTextEntryState.Refused);
			postgreSQL.DumpSqlText().Single(static e => e.Source == SqlTextCatalog.HostSource).State.ShouldBe(SqlTextEntryState.Allowed);
		}

		[Test]
		public void ProviderScopedTemplateIsAcceptedOnlyOnItsProvider()
		{
			// [Expression(ProviderName.PostgreSQL, ...)] on Sql.ZeroPad: partitioned by the attribute's configuration
			const string text = "Lpad({}::text,{},'0')";

			Allowed(new PostgreSQLService().DumpSqlText()).ShouldContain((SqlTextKind.Expression, text));
			Allowed(new SQLiteService()    .DumpSqlText()).ShouldNotContain((SqlTextKind.Expression, text));
		}

		[Test]
		public void EveryCatalogPartitionIsAProviderFamily()
		{
			var library  = typeof(DataOptions).Assembly;
			var catalog  = (ISqlTextCatalog)Activator.CreateInstance(library.GetType("LinqToDB.Remote.GeneratedSqlTextCatalog", throwOnError: true)!, nonPublic: true)!;
			const string prefix = "LinqToDB.Internal.DataProvider.";

			// the runtime takes a provider's family from the namespace of its provider type
			var families = library.GetTypes()
				.Where(static t => !t.IsAbstract && typeof(LinqToDB.DataProvider.IDataProvider).IsAssignableFrom(t) && t.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true)
				.Select(static t => t.Namespace!.Substring(prefix.Length).Split('.')[0])
				.ToHashSet(StringComparer.Ordinal);

			families.ShouldContain("SqlServer");

			var partitions = catalog.Items.Select(static i => i.Partition).Where(static p => p != null).Distinct();

			partitions.Where(p => !families.Contains(p!)).ShouldBeEmpty();
		}

		[Test]
		public void LibraryTextWithQuotedFormatItemIsNotAccepted()
		{
			var allowed = Allowed(new SQLiteService().DumpSqlText());

			// control: a library text with quotes beside its format items stays
			allowed.ShouldContain((SqlTextKind.Expression, "Lpad({},{},'0')"));

			allowed.ShouldNotContain((SqlTextKind.Expression, "format({}, 'd{}')"));
			allowed.ShouldNotContain((SqlTextKind.Expression, "printf('%0{}d', {})"));
		}

		[TestCase("MARKER ",          Description = "trailing space")]
		[TestCase("MARKER\u0001TEXT", Description = "control character")]
		[TestCase("MARKER\u00A0",     Description = "trailing non-ASCII space")]
		public void ExportEscapesWhitespaceAndControlCharacters(string text)
		{
			var service = new SQLiteService();

			service.SqlText.Add(SqlTextKind.QueryHint, text);

			using var writer = new StringWriter();

			service.SqlText.Export(writer);

			var exported = writer.ToString();

			foreach (var line in exported.Split('\n'))
			{
				var content = line.TrimEnd('\r');

				if (content.Length > 0)
					char.IsWhiteSpace(content[content.Length - 1]).ShouldBeFalse();
			}

			exported.ShouldNotContain("\u0001");

			var copy = new SQLiteService();

			copy.SqlText.Load(new StringReader(exported));

			copy.DumpSqlText().Single(static e => e.Source == SqlTextCatalog.HostSource).Text.ShouldBe(text);
		}

		const string LibraryText        = "Lpad({0},{1},'0')";
		const string LibraryCanonical   = "Lpad({},{},'0')";
		const string AssemblyText       = "ASSEMBLY_CATALOG_TEXT({})";
		const string AssemblyNoMatch    = "ASSEMBLY_REMOVAL_MATCHES_NOTHING({})";

		/// <summary>A catalog that allows one text and removes a library text and a text no source has.</summary>
		public sealed class RemovingCatalog : ISqlTextCatalog
		{
			public IReadOnlyList<SqlTextItem> Items { get; } =
			[
				new(SqlTextKind.Expression, AssemblyText,    null, nameof(RemotePolicyTests), false),
				new(SqlTextKind.Expression, LibraryText,     null, nameof(RemotePolicyTests), false, IsRemoval: true),
				new(SqlTextKind.Expression, AssemblyNoMatch, null, nameof(RemotePolicyTests), false, IsRemoval: true),
			];
		}

		// a fresh assembly, so its removals reach no other test the way a catalog of this test assembly would
		static Assembly CatalogAssembly()
		{
			var attribute = new CustomAttributeBuilder(typeof(SqlTextCatalogAttribute).GetConstructor([typeof(Type)])!, [typeof(RemovingCatalog)]);
			var name      = new AssemblyName("SqlTextCatalogAssembly" + Guid.NewGuid().ToString("N"));

			return AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run, [attribute]);
		}

		[Test]
		public void AssemblyCatalogRemovalAppliesToLibraryEntries()
		{
			var service  = new SQLiteService();
			var assembly = CatalogAssembly();
			var source   = assembly.GetName().Name;

			// control: the library entry is allowed until the assembly is registered
			service.DumpSqlText().Single(static e => e.Text == LibraryCanonical).State.ShouldBe(SqlTextEntryState.Allowed);

			service.SqlTextAssemblies.Add(assembly);

			var dump = service.DumpSqlText();

			var removed = dump.Single(static e => e.Text == LibraryCanonical);
			removed.State.ShouldBe(SqlTextEntryState.Removed);
			removed.Source.ShouldBe(source);

			var stale = dump.Single(static e => e.Text == AssemblyNoMatch);
			stale.State.ShouldBe(SqlTextEntryState.StaleRemoval);
			stale.Source.ShouldBe(source);

			var allowed = dump.Single(static e => e.Text == AssemblyText);
			allowed.State.ShouldBe(SqlTextEntryState.Allowed);
			allowed.Source.ShouldBe(source);
		}

		[Test]
		public void HostAdditionDoesNotOverrideAssemblyRemoval()
		{
			var service = new SQLiteService();

			service.SqlTextAssemblies.Add(CatalogAssembly());
			service.SqlText.Add(SqlTextKind.Expression, LibraryText);

			Allowed(service.DumpSqlText()).ShouldNotContain((SqlTextKind.Expression, LibraryCanonical));
		}

		[Test]
		public void ExportedAssemblyCatalogLoadsIntoHostCatalog()
		{
			using var writer = new StringWriter();

			SqlTextCatalog.Export(writer, CatalogAssembly());

			var service = new SQLiteService();

			service.SqlText.Load(new StringReader(writer.ToString()));

			var dump = service.DumpSqlText();

			dump.Single(static e => e.Text == AssemblyText).State.ShouldBe(SqlTextEntryState.Allowed);
			dump.Single(static e => e.Text == LibraryCanonical).State.ShouldBe(SqlTextEntryState.Removed);
		}

		[Test]
		public void LoadAcceptsDbTypeKind()
		{
			var service = new SQLiteService();

			service.SqlText.Load(new StringReader("+\tDbType\tMARKER_TYPE(10)"));

			service.DumpSqlText().Single(static e => e.Source == SqlTextCatalog.HostSource).Kind.ShouldBe(SqlTextKind.DbType);
		}

		[Test]
		public void LoadRejectsUnescapedTrailingWhitespace()
		{
			var service = new SQLiteService();

			Assert.Throws<FormatException>(() => service.SqlText.Load(new StringReader("-\tExtensionArgument\t, ")));
		}
	}
}
