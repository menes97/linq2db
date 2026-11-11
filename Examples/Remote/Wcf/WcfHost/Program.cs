using System;
using System.ServiceModel;
using System.ServiceModel.Description;

using LinqToDB.Remote;
using LinqToDB.Remote.Wcf;

namespace Host
{
	sealed class Program
	{
		static void Main(string[] args)
		{
			// This sample's model exists only in the client, so the server cannot vouch for its tables and the client is
			// trusted. A production host shares the model assembly, lists it in MappingAssemblies and keeps TrustedClient
			// off; see the Security section of the package readme.
			using var host = new ServiceHost(
				new WcfLinqService(new LinqService() { AllowUpdates = true, RemoteClientTag = "Wcf", TrustedClient = true }, true),
				new Uri("net.tcp://localhost:30304"));

			host.Description.Behaviors.Add(new ServiceMetadataBehavior());
			host.Description.Behaviors.Find<ServiceDebugBehavior>().IncludeExceptionDetailInFaults = true;
			host.AddServiceEndpoint(typeof(IMetadataExchange), MetadataExchangeBindings.CreateMexTcpBinding(), "mex");
			host.AddServiceEndpoint(
				typeof(IWcfLinqService),
				new NetTcpBinding(SecurityMode.None)
				{
					MaxReceivedMessageSize = 10000000,
					MaxBufferPoolSize      = 10000000,
					MaxBufferSize          = 10000000,
					CloseTimeout           = new TimeSpan(00, 01, 00),
					OpenTimeout            = new TimeSpan(00, 01, 00),
					ReceiveTimeout         = new TimeSpan(00, 10, 00),
					SendTimeout            = new TimeSpan(00, 10, 00),
				},
				"LinqOverWcf");

			host.Open();

			Console.WriteLine("Press any key to exit");
			Console.ReadKey();

			host.Close();
		}
	}
}
