using LinqToDB;
using LinqToDB.Configuration;
using LinqToDB.Data;
using LinqToDB.Remote;
using LinqToDB.Remote.Grpc;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ProtoBuf.Grpc.Server;

namespace Server
{
	public class Startup
	{
		public void ConfigureServices(IServiceCollection services)
		{
			// Set up Linq2DB connection
			DataConnection.DefaultSettings = new LinqToDBSettings(
				"Northwind",
				ProviderName.SqlServer,
				"Server=.;Database=Northwind;Trusted_Connection=True"
				);

			services.AddGrpc();
			services.AddCodeFirstGrpc();
			services.AddSingleton(p =>
				new GrpcLinqService(
					new LinqService()
					{
						AllowUpdates  = true,
						// This sample's model exists only in the client, so the server cannot vouch for its tables and the
						// client is trusted. A production host shares the model assembly, lists it in MappingAssemblies and
						// keeps TrustedClient off; see the Security section of the package readme.
						TrustedClient = true,
					},
					true
				));
		}

		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			app.UseRouting();

			app.UseEndpoints(endpoints =>
			{
				endpoints.MapGrpcService<GrpcLinqService>();
			});
		}
	}
}
