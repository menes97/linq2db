using System;
using System.Collections.Generic;

using LinqToDB.Data;
using LinqToDB.Mapping;
using LinqToDB.Remote;

namespace Tests.Remote
{
	internal sealed class TestLinqService : LinqService, ITestLinqService
	{
		readonly Func<string?, MappingSchema?, DataConnection> _connectionFactory;

		public TestLinqService(Func<string?, MappingSchema?, DataConnection> connectionFactory)
		{
			_connectionFactory = connectionFactory;
			TrustedClient      = _untrustedLog == null;

			if (_untrustedLog != null)
			{
				MappingAssemblies.Add(typeof(TestLinqService).Assembly);
				MappingAssemblies.Add(typeof(Tests.Model.Person).Assembly);

				if (System.Reflection.Assembly.GetEntryAssembly() is { } entry)
					MappingAssemblies.Add(entry);

				foreach (var provider in TestConfiguration.UserProviders)
				{
					AllowedConfigurations.Add(provider);
					AllowedConfigurations.Add(provider + ".LinqService");
				}
			}
		}

		static readonly string? _untrustedLog = Environment.GetEnvironmentVariable("LINQ2DB_TESTS_UNTRUSTED_REMOTE_LOG");

		/// <summary>Whether remote test hosts run with <see cref="LinqService.TrustedClient"/> off.</summary>
		internal static bool IsUntrusted => _untrustedLog != null;

		static readonly List<RemoteRejection> _rejections = new();

		/// <summary>
		/// Rejections recorded since the last <see cref="ClearRejections"/>. The client sees only the transport's text,
		/// so <see cref="RejectedWhenUntrustedAttribute"/> reads the typed rejections here. Remote tests run one at a time.
		/// </summary>
		internal static RemoteRejection[] GetRejections()
		{
			lock (_rejections)
				return _rejections.ToArray();
		}

		internal static void ClearRejections()
		{
			lock (_rejections)
				_rejections.Clear();
		}

		protected override void HandleException(Exception exception)
		{
			if (_untrustedLog != null && exception is RemoteRequestRejectedException rejected)
			{
				lock (_rejections)
				{
					_rejections.AddRange(rejected.Rejections);

					foreach (var r in rejected.Rejections)
						System.IO.File.AppendAllText(_untrustedLog, $"{r.Reason}\t{r.Kind}\t{r.NodeType}\t{r.Text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}{Environment.NewLine}");
				}
			}
		}

		public override DataConnection CreateDataContext(string? configuration)
		{
			// Tie this server request to the originating remote test (by provider) so its query
			// trace/baseline logging resolves that test's context, not whatever remote test is
			// currently active. AsyncLocal flows through the request's async query execution.
			CustomTestContext.SetServerProvider(configuration);

			return _connectionFactory(configuration, MappingSchema);
		}
	}
}

