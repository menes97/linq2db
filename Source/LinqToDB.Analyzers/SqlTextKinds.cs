using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace LinqToDB.Analyzers
{
	/// <summary>The <c>SqlTextKind</c> members of the referenced linq2db, by name and value.</summary>
	sealed class SqlTextKinds
	{
		readonly Dictionary<int, string> _byValue = new();
		readonly HashSet<string>         _names   = new(StringComparer.Ordinal);

		public SqlTextKinds(INamedTypeSymbol kindType)
		{
			foreach (var field in kindType.GetMembers().OfType<IFieldSymbol>())
			{
				if (field is { HasConstantValue: true, ConstantValue: int value })
				{
					_byValue[value] = field.Name;
					_names.Add(field.Name);
				}
			}
		}

		public string? FromValue(int value) => _byValue.TryGetValue(value, out var name) ? name : null;

		/// <summary>The value of <paramref name="name"/>, or -1 when the referenced linq2db has no such kind.</summary>
		public int ToValue(string name)
		{
			foreach (var pair in _byValue)
				if (string.Equals(pair.Value, name, StringComparison.Ordinal))
					return pair.Key;

			return -1;
		}

		public bool Contains(string name) => _names.Contains(name);
	}
}
