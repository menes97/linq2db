#if !NETFRAMEWORK
using System;
using System.Linq;
using System.Threading.Tasks;

using LinqToDB.Remote.HttpClient.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using NUnit.Framework;

using Shouldly;

namespace Tests.Linq
{
	/// <summary>
	/// MVC discovers the public controllers of every application part, and a web project adds its referenced assemblies as
	/// parts, so the server assembly must not hold a public concrete controller that serves the remote protocol by itself.
	/// </summary>
	[TestFixture]
	public class RemoteHttpControllerTests
	{
		static readonly string[] _actions = ["ExecuteBatchAsync", "ExecuteNonQueryAsync", "ExecuteReaderAsync", "ExecuteScalarAsync", "GetInfoAsync"];

		static string[] Expected(string route) => _actions.Select(a => $"{route}/{a}/{{configuration?}}").Order(StringComparer.Ordinal).ToArray();

		static async Task<string?[]> GetRoutes(Action<IMvcBuilder>? configure = null)
		{
			var builder = WebApplication.CreateSlimBuilder();
			var mvc     = builder.Services.AddControllers().ConfigureApplicationPartManager(static apm =>
			{
				apm.ApplicationParts.Clear();
				apm.ApplicationParts.Add(new AssemblyPart(typeof(LinqToDBController).Assembly));
			});

			configure?.Invoke(mvc);

			await using var app = builder.Build();

			return app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
				.Select(static a => a.AttributeRouteInfo?.Template)
				.Order(StringComparer.Ordinal)
				.ToArray();
		}

		[Test]
		public async Task ControllerIsNotServedUnlessRegistered()
		{
			(await GetRoutes()).ShouldBeEmpty();
		}

		[Test]
		public async Task RegisteredControllerIsServedAtItsRouteOnly()
		{
			(await GetRoutes(static b => b.AddLinqToDBController())).ShouldBe(Expected("api/linq2db"));
		}

		[Test]
		public async Task GenericRegistrationIsServedAtItsRouteOnly()
		{
			(await GetRoutes(static b => b.AddLinqToDBController<LinqToDB.Data.DataConnection>("api/test"))).ShouldBe(Expected("api/test"));
		}
	}
}
#endif
