using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

using LinqToDB.Data;
using LinqToDB.Mapping;
using LinqToDB.Remote;

namespace Tests.Remote
{
	public sealed class TestLinqService : LinqService, ITestLinqService
	{
		readonly Func<string?, MappingSchema?, DataConnection> _connectionFactory;

		static readonly List<Assembly> _testAssemblies = new();

		/// <summary>
		/// Registers a test assembly whose mappings and <see cref="SqlTextCatalogAttribute"/> catalogs untrusted remote test
		/// hosts accept. Call it from the assembly's <c>[SetUpFixture]</c>, before the first remote test creates a host.
		/// </summary>
		public static void RegisterTestAssembly(Assembly assembly)
		{
			lock (_testAssemblies)
				if (!_testAssemblies.Contains(assembly))
					_testAssemblies.Add(assembly);
		}

		internal TestLinqService(Func<string?, MappingSchema?, DataConnection> connectionFactory)
		{
			_connectionFactory = connectionFactory;
			TrustedClient      = !IsUntrusted;

			if (IsUntrusted)
			{
				MappingAssemblies.Add(typeof(TestLinqService).Assembly);
				MappingAssemblies.Add(typeof(Tests.Model.Person).Assembly);

				lock (_testAssemblies)
					foreach (var assembly in _testAssemblies)
						MappingAssemblies.Add(assembly);

				// DataContextTests.TestNullConfiguration_* send no configuration name
				AllowedConfigurations.Add(null);

				foreach (var provider in TestConfiguration.UserProviders)
				{
					AllowedConfigurations.Add(provider);
					AllowedConfigurations.Add(provider + ".LinqService");

					// RemoteContextTests.TestFlagsTransfered registers "<context>:<transport>" for its own provider
					foreach (var transport in typeof(TestBase.RemoteTransport).GetEnumNames())
						AllowedConfigurations.Add($"{provider}.LinqService:{transport}");
				}
			}
		}

		// optional: a file that receives every rejection, one line each, for a census of a run
		static readonly string? _untrustedLog = Environment.GetEnvironmentVariable("LINQ2DB_TESTS_UNTRUSTED_REMOTE_LOG");

		/// <summary>
		/// Whether remote test hosts run with <see cref="LinqService.TrustedClient"/> off: the default, so the remote suite
		/// exercises the untrusted-client policy. <c>LINQ2DB_TESTS_TRUSTED_REMOTE=1</c> runs them trusted.
		/// </summary>
		internal static bool IsUntrusted { get; } = Environment.GetEnvironmentVariable("LINQ2DB_TESTS_TRUSTED_REMOTE") == "0";

		// Keyed by provider: a provider's remote tests run one at a time on its lane, but providers run concurrently.
		static readonly ConcurrentDictionary<string, List<RemoteRejection>> _rejections = new(StringComparer.OrdinalIgnoreCase);

		// the provider of the current server request, set in CreateDataContext before the request is validated
		static readonly AsyncLocal<string?> _requestProvider = new();

		static readonly Lock _logLock = new();

		/// <summary>
		/// Rejections recorded for <paramref name="provider"/> since the last <see cref="ClearRejections"/>. The client sees
		/// only the transport's text, so <see cref="RejectedWhenUntrustedAttribute"/> reads the typed rejections here.
		/// </summary>
		internal static RemoteRejection[] GetRejections(string provider)
		{
			if (!_rejections.TryGetValue(provider, out var list))
				return [];

			lock (list)
				return list.ToArray();
		}

		internal static void ClearRejections(string provider) => _rejections.TryRemove(provider, out _);

		protected override void HandleException(Exception exception)
		{
			if (IsUntrusted && exception is RemoteRequestRejectedException rejected)
			{
				var list = _rejections.GetOrAdd(_requestProvider.Value ?? string.Empty, static _ => new());

				lock (list)
					list.AddRange(rejected.Rejections);

				if (_untrustedLog != null)
					lock (_logLock)
						foreach (var r in rejected.Rejections)
							System.IO.File.AppendAllText(_untrustedLog, $"{r.Reason}\t{r.Kind}\t{r.NodeType}\t{r.Text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}{Environment.NewLine}");
			}
		}

		public override DataConnection CreateDataContext(string? configuration)
		{
			_requestProvider.Value = configuration?.StripRemote();

			// Tie this server request to the originating remote test (by provider) so its query
			// trace/baseline logging resolves that test's context, not whatever remote test is
			// currently active. AsyncLocal flows through the request's async query execution.
			CustomTestContext.SetServerProvider(configuration);

			return _connectionFactory(configuration, MappingSchema);
		}
	}
}

