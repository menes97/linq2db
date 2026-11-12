using System.Threading.Tasks;

using CodeGenerators;

using NUnit.Framework;

namespace Tests.Analyzers.Internal
{
	[TestFixture]
	public class SqlTextContractAnalyzerTests
	{
		// A miniature of the [SqlText] contract: the analyzer keys on the attribute's full name and reads the kind
		// as its constructor argument, so a local copy stands in for LinqToDB.Remote.
		const string Model = """
			namespace LinqToDB.Remote
			{
				enum SqlTextKind { Expression, Fragment }

				[System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Property)]
				sealed class SqlTextAttribute : System.Attribute
				{
					public SqlTextAttribute(SqlTextKind kind) { }
				}
			}

			namespace Subject
			{
				using System.Collections.Generic;

				using LinqToDB.Remote;

				sealed class Fragment
				{
					public Fragment([SqlText(SqlTextKind.Fragment)] string text) { }
				}

				sealed class Builder
				{
					[SqlText(SqlTextKind.Expression)]
					public string? Expression { get; set; }
				}

				enum Part { Day, Hour }

				static partial class Uses
				{
			{{body}}
				}
			}
			""";

		static Task Verify(string body) => AnalyzerVerifier<SqlTextContractAnalyzer>.VerifyAsync(Model.Replace("{{body}}", body));

		// a second part of Subject.Uses, in its own file
		static Task Verify(string body, string otherPart) => AnalyzerVerifier<SqlTextContractAnalyzer>.VerifyAsync(
		[
			Model.Replace("{{body}}", body),
			$$"""
			namespace Subject
			{
				static partial class Uses
				{
			{{otherPart}}
				}
			}
			""",
		]);

		[Test]
		public Task TextFromCallerIsReported() => Verify("""
					public static Fragment M(string input) => new Fragment({|LINQ2DB0007:input|});
			""");

		[Test]
		public Task ConstantIsAccepted() => Verify("""
					const string Text = "ORDER BY {0}";

					public static Fragment M1() => new Fragment("{0} DESC");
					public static Fragment M2() => new Fragment(Text);
			""");

		[Test]
		public Task ForwardedSqlTextParameterIsAccepted() => Verify("""
					public static Fragment M([SqlText(SqlTextKind.Fragment)] string text) => new Fragment(text);
			""");

		[Test]
		public Task LocalAssignedOnlyConstantsIsAccepted() => Verify("""
					public static Fragment M(bool desc)
					{
						var text = "{0}";

						if (desc)
							text = "{0} DESC";

						return new Fragment(text);
					}
			""");

		[Test]
		public Task LocalAssignedCallerTextIsReported() => Verify("""
					public static Fragment M(bool desc, string input)
					{
						var text = "{0}";

						if (desc)
							text = input;

						return new Fragment({|LINQ2DB0007:text|});
					}
			""");

		[Test]
		public Task TupleDeconstructionFromSwitchIsAccepted() => Verify("""
					public static Fragment M(Part part)
					{
						var (text, divider) = part switch
						{
							Part.Day  => ("DAYS({0})",  1),
							Part.Hour => ("HOURS({0})", 24),
							_         => throw new System.ArgumentOutOfRangeException(nameof(part)),
						};

						return new Fragment(text);
					}
			""");

		[Test]
		public Task ReadOnlyDictionaryValueIsAccepted() => Verify("""
					static readonly IReadOnlyDictionary<Part, string> Names = new Dictionary<Part, string>
					{
						{ Part.Day,  "toDay"  },
						{ Part.Hour, "toHour" },
					};

					public static Fragment M(Part part)
					{
						Names.TryGetValue(part, out var name);

						return new Fragment(name + "OrNull");
					}
			""");

		[Test]
		public Task AppendsAreAccepted() => Verify("""
					public static Fragment M(Part part)
					{
						var text = "DATEDIFF('";

						text += part == Part.Day ? "d" : "h";
						text += "', {0}, {1})";

						return new Fragment(text);
					}
			""");

		// G2: a [SqlText] operand has no values of its own, so the composed text would go uncatalogued
		[Test]
		public Task SqlTextParameterInConcatIsReported() => Verify("""
					public static Fragment M([SqlText(SqlTextKind.Fragment)] string text) => new Fragment({|LINQ2DB0007:text + " DESC"|});
			""");

		[Test]
		public Task SqlTextParameterInInterpolationIsReported() => Verify("""
					public static Fragment M([SqlText(SqlTextKind.Fragment)] string text) => new Fragment({|LINQ2DB0007:$"{text} DESC"|});
			""");

		// G3: `+=` composes from the property's earlier value, which no site shows
		[Test]
		public Task AppendToSqlTextPropertyIsReported() => Verify("""
					public static void M(Builder builder)
					{
						{|LINQ2DB0007:builder.Expression += " NULLS FIRST"|};
					}
			""");

		[Test]
		public Task CoalesceAssignmentOfCallerTextIsReported() => Verify("""
					public static void M(Builder builder, string input)
					{
						builder.Expression ??= {|LINQ2DB0007:input|};
					}
			""");

		[Test]
		public Task DeconstructionIntoSqlTextPropertyIsReported() => Verify("""
					public static void M(Builder builder)
					{
						{|LINQ2DB0007:(builder.Expression, var other) = ("{0}", 1)|};
					}
			""");

		// G5: a private method's parameter resolves from its calls only when they are all in view
		[Test]
		public Task ParameterResolvedFromCallersIsAccepted() => Verify("""
					static Fragment Build(string text) => new Fragment(text);

					public static Fragment M1() => Build("{0}");
					public static Fragment M2() => Build("{0} DESC");
			""");

		[Test]
		public Task ParameterOfMethodUsedAsMethodGroupIsReported() => Verify("""
					static Fragment Build(string text) => new Fragment({|LINQ2DB0007:text|});

					public static Fragment M() => Build("{0}");
					public static System.Func<string, Fragment> Factory() => Build;
			""");

		[Test]
		public Task ParameterOfTypeSplitAcrossFilesIsReported() => Verify("""
					static Fragment Build(string text) => new Fragment({|LINQ2DB0007:text|});

					public static Fragment M() => Build("{0}");
			""",
			"""
					public static Fragment Other() => Build("{0} DESC");
			""");

		// G6: a write through out or ref has no value to read
		[Test]
		public Task LocalWrittenThroughOutIsReported() => Verify("""
					static void Choose(out string text) => text = "{0}";

					public static Fragment M()
					{
						var text = "{0} DESC";

						Choose(out text);

						return new Fragment({|LINQ2DB0007:text|});
					}
			""");

		// G7: a forwarded [SqlText] parameter is catalogued under its own kind, so another kind cannot accept it
		[Test]
		public Task ForwardedParameterOfOtherKindIsReported() => Verify("""
					public static Fragment M([SqlText(SqlTextKind.Expression)] string text) => new Fragment({|LINQ2DB0007:text|});
			""");

		[Test]
		public Task AppendInLoopIsReported() => Verify("""
					public static Fragment M(int count)
					{
						var text = "{0}";

						for (var i = 1; i < count; i++)
							text += ", {0}";

						return new Fragment({|LINQ2DB0007:text|});
					}
			""");
	}
}
