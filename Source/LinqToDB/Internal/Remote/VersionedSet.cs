using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace LinqToDB.Internal.Remote
{
	/// <summary>A set whose <see cref="Version"/> changes on every edit, so a cache built from its contents can tell it is stale.</summary>
	internal sealed class VersionedSet<T> : ICollection<T>
	{
		readonly HashSet<T> _items;

		int _version;

		public VersionedSet(IEqualityComparer<T>? comparer = null)
		{
			_items = new HashSet<T>(comparer);
		}

		public int Version => Volatile.Read(ref _version);

		public int  Count      => _items.Count;
		public bool IsReadOnly => false;

		public void Add(T item)
		{
			if (_items.Add(item))
				Interlocked.Increment(ref _version);
		}

		public bool Remove(T item)
		{
			if (!_items.Remove(item))
				return false;

			Interlocked.Increment(ref _version);
			return true;
		}

		public void Clear()
		{
			_items.Clear();
			Interlocked.Increment(ref _version);
		}

		public bool Contains(T item)                => _items.Contains(item);
		public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

		public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
