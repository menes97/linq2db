using System;
using System.Threading.Tasks;

using LinqToDB;
using LinqToDB.Data;
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
		public void TrustedClientIgnoresList()
		{
			var service = new CountingService { TrustedClient = true };

			Assert.ThrowsAsync<InvalidOperationException>(async () => await Call(service, "Reader", Blocked));

			service.Created.ShouldBe(1);
		}
	}
}
