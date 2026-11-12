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

			// with T = DataConnection itself, registering its assembly would let clients name any library type as an entity
			if (typeof(T).Assembly != typeof(DataConnection).Assembly)
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
