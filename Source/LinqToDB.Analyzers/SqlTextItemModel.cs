using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace LinqToDB.Analyzers
{
	/// <summary>One catalog entry the generator emits.</summary>
	readonly record struct SqlTextItemModel(string Kind, string Text, string? Origin, bool IsTemplate);
}
