using LinqToDB.Data;

namespace LinqToDB.Remote
{
	public class LinqService<T> : LinqService, ILinqService<T>
		where T : IDataContext
	{
		readonly IDataContextFactory<T> _dataContextFactory;

		public LinqService(IDataContextFactory<T> dataContextFactory)
		{
			_dataContextFactory = dataContextFactory;

			MappingAssemblies.Add(typeof(T).Assembly);
		}

		public override DataConnection CreateDataContext(string? configuration)
		{
			var dc = _dataContextFactory.CreateDataContext(configuration) as DataConnection ?? throw new LinqToDBException($"Type '{typeof(T)}' should implement 'DataConnection'.");

			dc.Tag = RemoteClientTag;

			return dc;
		}
	}
}
