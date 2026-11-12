using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using LinqToDB;
using LinqToDB.Analyzers;
using LinqToDB.Remote;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Tests.Analyzers
{
	/// <summary>A <c>LinqToDBSqlText</c> file passed to the generator.</summary>
	internal sealed record SqlTextFile(string Path, string Text, bool IsCatalogFile = true);

	/// <summary>Result of one generator run: diagnostics, the updated compilation and, when it compiles, the catalog items.</summary>
	internal sealed record GeneratorResult(
		ImmutableArray<Diagnostic> Diagnostics,
		Compilation                Output,
		GeneratorRunResult         Run,
		IReadOnlyList<SqlTextItem> Items)
	{
		public string? GeneratedSource => Run.GeneratedSources.Length == 0 ? null : Run.GeneratedSources[0].SourceText.ToString();

		public IEnumerable<string> Texts(SqlTextKind kind) => Items.Where(i => i.Kind == kind).Select(static i => i.Text);
	}

	// Drives SqlTextUserCatalogGenerator through CSharpGeneratorDriver: the snippet compiles against the .NET 8 reference
	// assemblies plus the real linq2db, the output is emitted and loaded, and the generated catalog is read through the
	// public ISqlTextCatalog contract, so the items are what the runtime would read.
	internal static class GeneratorVerifier
	{
		public const string ProjectDir = "/proj";

		static ImmutableArray<MetadataReference>? _references;

		public static async Task<ImmutableArray<MetadataReference>> GetReferencesAsync(bool withLinqToDB = true)
		{
			_references ??= await TestReferenceAssemblies.Default.ResolveAsync(LanguageNames.CSharp, CancellationToken.None).ConfigureAwait(false);

			return withLinqToDB
				? _references.Value.Add(MetadataReference.CreateFromFile(typeof(Sql).Assembly.Location))
				: _references.Value;
		}

		public static async Task<GeneratorResult> RunAsync(
			string                     source,
			bool                       enabled         = true,
			IEnumerable<SqlTextFile>?  files           = null,
			string?                    roots           = null,
			LanguageVersion            languageVersion = LanguageVersion.Latest,
			bool                       withLinqToDB    = true,
			string                     assemblyName    = "UserAssembly",
			IEnumerable<MetadataReference>? extraReferences = null)
		{
			var parseOptions = new CSharpParseOptions(languageVersion);
			var references   = (await GetReferencesAsync(withLinqToDB).ConfigureAwait(false)).AddRange(extraReferences ?? []);

			var compilation = CSharpCompilation.Create(
				assemblyName,
				[CSharpSyntaxTree.ParseText(source, parseOptions, path: ProjectDir + "/Source.cs")],
				references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

			var global = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["build_property.LinqToDBSqlTextProjectDir"]     = ProjectDir,
				["build_property.LinqToDBSqlTextIntermediateDir"] = "obj/",
			};

			if (enabled)
				global["build_property.LinqToDBSqlTextCatalog"] = "true";

			if (roots != null)
				global["build_property.LinqToDBSqlTextRoots"] = roots;

			var texts       = (files ?? []).Select(static f => new TestAdditionalText(f.Path, f.Text, f.IsCatalogFile)).ToImmutableArray();
			var options     = new TestOptionsProvider(global, texts);
			var generator   = new SqlTextUserCatalogGenerator().AsSourceGenerator();

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[generator],
				texts.CastArray<AdditionalText>(),
				parseOptions,
				options,
				new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

			driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

			return new GeneratorResult(diagnostics, output, driver.GetRunResult().Results[0], LoadItems(output));
		}

		// Emits and loads the output; returns no items when nothing was generated or it does not compile (asserted separately).
		static IReadOnlyList<SqlTextItem> LoadItems(Compilation output)
		{
			using var stream = new MemoryStream();

			if (!output.Emit(stream).Success)
				return [];

			var assembly = Assembly.Load(stream.ToArray());

			return assembly.GetCustomAttributes<SqlTextCatalogAttribute>()
				.SelectMany(static a => ((ISqlTextCatalog)Activator.CreateInstance(a.CatalogType, nonPublic: true)!).Items)
				.ToList();
		}

		public static IEnumerable<Diagnostic> Errors(this Compilation compilation)
		{
			return compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error);
		}

		sealed class TestAdditionalText(string path, string text, bool isCatalogFile) : AdditionalText
		{
			public override string Path { get; } = path;

			public bool IsCatalogFile { get; } = isCatalogFile;

			public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
		}

		sealed class TestOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
		{
			public override bool TryGetValue(string key, out string value)
			{
				if (values.TryGetValue(key, out var found))
				{
					value = found;
					return true;
				}

				value = null!;
				return false;
			}
		}

		sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
		{
			readonly Dictionary<AdditionalText, AnalyzerConfigOptions> _files = new();
			static readonly AnalyzerConfigOptions _empty = new TestOptions(new Dictionary<string, string>(StringComparer.Ordinal));

			public TestOptionsProvider(IReadOnlyDictionary<string, string> global, ImmutableArray<TestAdditionalText> files)
			{
				GlobalOptions = new TestOptions(global);

				foreach (var file in files)
				{
					_files[file] = file.IsCatalogFile
						? new TestOptions(new Dictionary<string, string>(StringComparer.Ordinal) { ["build_metadata.AdditionalFiles.LinqToDBSqlText"] = "true" })
						: _empty;
				}
			}

			public override AnalyzerConfigOptions GlobalOptions { get; }

			public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _empty;

			public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _files.TryGetValue(textFile, out var options) ? options : _empty;
		}
	}
}
