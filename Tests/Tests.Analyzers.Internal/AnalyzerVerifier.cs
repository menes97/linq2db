using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Tests.Analyzers.Internal
{
	// Thin wrapper over the Roslyn testing SDK for the repo-internal (LINQ2DB0xxx) analyzers. Unlike the
	// Tests.Analyzers verifier this adds no linq2db reference: the rules under test are about types internal to
	// LinqToDB, so each snippet declares its own copy of the model it is analyzed against.
	internal static class AnalyzerVerifier<TAnalyzer>
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		public static Task VerifyAsync(string source, params DiagnosticResult[] expected)
		{
			var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
			{
				TestCode            = source,
				ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
			};

			test.ExpectedDiagnostics.AddRange(expected);

			return test.RunAsync(CancellationToken.None);
		}

		/// <summary>Verifies a compilation of several files, for rules that depend on how a type is split across them.</summary>
		public static Task VerifyAsync(string[] sources)
		{
			var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
			{
				ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
			};

			foreach (var source in sources)
				test.TestState.Sources.Add(source);

			return test.RunAsync(CancellationToken.None);
		}
	}
}
