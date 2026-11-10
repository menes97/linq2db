using System;

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

		protected override void HandleException(Exception exception)
		{
			if (_untrustedLog != null && exception is RemoteRequestRejectedException rejected)
			{
				lock (typeof(TestLinqService))
				{
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

