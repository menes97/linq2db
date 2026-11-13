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

		public bool Contains(string name) => _names.Contains(name);
	}
}
