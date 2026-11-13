using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using LinqToDB;
using LinqToDB.Analyzers;

using NUnit.Framework;

using Shouldly;

namespace Tests.Analyzers
{
	/// <summary>
	/// Every public linq2db member that takes text gets a disposition: catalogued by a generator rule, annotated with
	/// <c>[SqlText]</c>, or one of the classes below. A new text-taking member fails here until it is classified, so the
	/// generator's call-site rules cannot silently fall behind the public surface.
	/// </summary>
	[TestFixture]
	public sealed class SqlTextCallSiteGuardTests
	{
		enum Disposition
		{
			/// <summary>Rendered as an identifier, checked by the validator's identifier rule; never catalogued.</summary>
			Identifier,
			/// <summary>Sent as a value (a parameter or a literal value), never as SQL text.</summary>
			Value,
			/// <summary>Rejected for untrusted clients by design (parent plan P10).</summary>
			NotCatalogued,
			/// <summary>Not reachable over a remote context: direct ADO.NET, configuration, schema, tooling.</summary>
			NotOnWire,
			/// <summary>Engine API under <c>LinqToDB.Internal</c>: its AST fields have validator dispositions (parent TO-25).</summary>
			Engine,
			/// <summary>A <c>Sql.*Attribute</c> member: its text is catalogued by the attribute rule (SqlTextAttributeSources).</summary>
			Attribute,
			/// <summary>Server-side mapping metadata, compared with the server's own descriptors, not with the catalog.</summary>
			Mapping,
			/// <summary>A DbType: the validator's closed DbType grammar or exact match applies (kind DbType, U-B11).</summary>
			DbType,
		}

		readonly record struct Rule(Regex Type, Regex Member, Regex Parameter, Disposition Disposition);

		static Rule R(string type, string member, string parameter, Disposition disposition)
		{
			return new(new Regex("^(?:" + type + ")$"), new Regex("^(?:" + member + ")$"), new Regex("^(?:" + parameter + ")$"), disposition);
		}

		// Keyed by (type, member, parameter) patterns, never by a parameter name alone: a seen name on a new type must fail.
		static readonly Rule[] _rules =
		[
			R(@"LinqToDB\.Internal\..*",                                       ".*",                   ".*",                            Disposition.Engine),
			R(@"LinqToDB\.SqlQuery\.SqlObjectName",                            ".*",                   ".*",                            Disposition.Engine),
			R(@"LinqToDB\.Sql\.(ISqlExtensionBuilder|SqlExtension|SqlExtensionParam|SqlQueryExtensionData)", ".*", ".*",              Disposition.Engine),
			R(@"LinqToDB\.ExtensionBuilderExtensions",                         "AddFragment|AddParameter", "name",                      Disposition.Engine),
			R(@"LinqToDB\.ExtensionBuilderExtensions",                         "AddParameter",         "value",                         Disposition.Value),

			// Sql.*Attribute: text members catalogued by the attribute rule; configuration and sqlProvider select a provider
			R(@"LinqToDB\.Sql\.(Expression|Function|Property|Extension|TableFunction|TableExpression|QueryExtension)Attribute", ".*", ".*", Disposition.Attribute),

			// direct ADO.NET: CommandInfo rejects any context other than DataContext or DataConnection (CommandInfo.cs:75)
			R(@"LinqToDB\.Data\.(DataConnection|DataContextExtensions|CommandInfo|DataParameter|LegacyMergeExtensions|TraceInfo|ConnectionOptions)", ".*", ".*", Disposition.NotOnWire),
			R(@"LinqToDB\.(DataContext|DataOptionsExtensions|QuerySql|RawSqlString|ServerSideOnlyException|LinqToDBException)", ".*", ".*", Disposition.NotOnWire),
			R(@"LinqToDB\.(Configuration|Common\.Configuration|SchemaProvider|Remote|Metadata|Reflection)(\..*)?", ".*", ".*",           Disposition.NotOnWire),
			R(@"LinqToDB\.(Linq\.LinqException|Common\.LinqToDBConvertException)", ".*",              ".*",                            Disposition.NotOnWire),
			R(@"LinqToDB\.DataProvider\.(IDataProvider|DataProviderParameterContext|\w+\.\w+Tools)", ".*", ".*",                       Disposition.NotOnWire),
			R(@"LinqToDB\.(ExpressionMethodAttribute|ExprParameterAttribute)",  ".*",                   ".*",                            Disposition.NotOnWire),
			R(@"LinqToDB\.Linq\.Expressions",                                  "MapMember|MapBinary",  "providerName",                  Disposition.NotOnWire),
			R(@"LinqToDB\.Linq\.Translation\.ITranslationContext",             "CreateErrorExpression", "message",                      Disposition.NotOnWire),
			R(@"LinqToDB\.LinqExtensions",                                     "IgnoreFilters",        "filterKeys",                    Disposition.NotOnWire),

			// tags are dropped for untrusted clients (parent E-1); a collation is free text (parent P10)
			R(@"LinqToDB\.LinqExtensions",                                     "TagQuery",             "tagValue",                      Disposition.NotCatalogued),
			R(@"LinqToDB\.Sql",                                                "Collate",              "collation",                     Disposition.NotCatalogued),

			// arguments that become parameters or values
			R(@"LinqToDB\.DataProvider\.SqlServer\.SqlFn(\..*)?",              ".*",                   ".*",                            Disposition.Value),
			R(@"LinqToDB\.Sql",                                                ".*",                   "str|substring|pattern|matchExpression|newValue|oldValue|separator|source|args|arguments|characterExpression|newString|replaceWithExpression|expression|expr", Disposition.Value),
			R(@"LinqToDB\.Linq\.Expressions",                                  ".*",                   "str|value",                     Disposition.Value),
			R(@"LinqToDB\.AnalyticFunctions",                                  "ListAgg",              "delimiter",                     Disposition.Value),
			R(@"LinqToDB\.DataProvider\.SqlServer\.SqlServerExtensions",       ".*",                   "search|term|language|property", Disposition.Value),
			R(@"LinqToDB\.DataProvider\.SQLite\.SQLiteExtensions",             ".*",                   "ellipses|endMatch|startMatch|format|function|match", Disposition.Value),
			R(@"LinqToDB\.DataProvider\.PostgreSQL\.PostgreSQLExtensions",     ".*",                   "delimiter|nullString|str",      Disposition.Value),
			R(@"LinqToDB\.DataProvider\.MySql\.MySqlExtensions",               ".*",                   "search",                        Disposition.Value),

			// identifiers (validator identifier rule, D-10)
			R(@"LinqToDB\.Sql",                                                "TableField|Property|TableAlias|TableName|TableSpec", "fieldName|propertyName|id", Disposition.Identifier),
			R(@"LinqToDB\.Sql\.SqlID",                                         ".*",                   ".*",                            Disposition.Identifier),
			R(@"LinqToDB\.LinqExtensions",                                     "AsCte",                "name",                          Disposition.Identifier),
			R(@"LinqToDB\.LinqExtensions",                                     "AsSubQuery|QueryName", "queryName",                     Disposition.Identifier),
			R(@"LinqToDB\.LinqExtensions",                                     "TableID",              "id",                            Disposition.Identifier),
			R(@"LinqToDB\.LinqExtensions",                                     ".*Hint",               "hintParameters?",               Disposition.Identifier),
			R(@"LinqToDB\.DataExtensions",                                     "GetCte",               "cteTableName",                  Disposition.Identifier),
			R(@"LinqToDB\.ICteBuilder",                                        "HasName",              "name",                          Disposition.Identifier),

			// provider hint wrappers ([ExpressionMethod] forwarders): arguments become hintParameter(s), identifier keys;
			// free text they build into the hint is rejected unless the host adds it (parent P10)
			R(@"LinqToDB\.DataProvider\.\w+\.\w+Hints(\.\w+)?",                ".*",                   "hintParameters?|indexNames?|queryBlock|columns|tableIDs", Disposition.Identifier),
			R(@"LinqToDB\.DataProvider\.\w+\.\w+Hints(\.\w+)?",                ".*",                   "values?|hint|hintFormat|parameters|innerDistribution|outerDistribution", Disposition.NotCatalogued),

			// DbType text: closed grammar or exact server match (V:209), kind DbType after F1 (U-B11)
			R(@"LinqToDB\.DbDataType",                                         ".ctor|WithDbType",     "dbType|dbName",                 Disposition.DbType),
			R(@"LinqToDB\.SqlQuery\.SqlDataType",                              ".ctor",                "dbType",                        Disposition.DbType),
			R(@"LinqToDB\.DataProvider\.SqlServer\.SqlType",                   ".ctor",                "dataType",                      Disposition.DbType),

			R(@"LinqToDB\.Mapping\..*",                                        ".*",                   ".*",                            Disposition.Mapping),
		];

		static readonly HashSet<string> _validatorKeys = new(StringComparer.Ordinal)
		{
			"hint", "hint2", "hintFormat", "values", "expression", "hintParameter", "hintParameters", "indexName",
		};

		internal readonly record struct TextMember(string Type, string Member, string Parameter, bool IsHintMethod, bool IsAnnotated, bool IsProperty);

		[Test]
		public void EveryTextMemberHasADisposition()
		{
			var unclassified = Unclassified(EnumerateReflected().Concat(EnumerateTfmOnly()))
				.ToList();

			var report = Environment.GetEnvironmentVariable("LINQ2DB_SQLTEXT_GUARD_REPORT");

			if (!string.IsNullOrEmpty(report))
				File.WriteAllLines(report, unclassified.Select(static m => m.Type + "\t" + m.Member + "\t" + m.Parameter));

			unclassified
				.Select(static m => m.Type + "." + m.Member + "(" + m.Parameter + ")")
				.Take(200)
				.ShouldBeEmpty(
					unclassified.Count + " public text members have no disposition; classify them in SqlTextCallSites or in this guard's rules");
		}

		[Test]
		public void UnseenMember_IsUnclassified()
		{
			Unclassified([new TextMember("LinqToDB.LinqExtensions", "NewHintApi", "neverSeenName", false, false, false)])
				.ShouldNotBeEmpty();
		}

		[Test]
		public void SeenTextKey_OnUnmatchedType_IsUnclassified()
		{
			Unclassified([new TextMember("LinqToDB.Data.NewExtensions", "Merge", "hint", false, false, false)])
				.ShouldNotBeEmpty();
		}

		static IEnumerable<TextMember> Unclassified(IEnumerable<TextMember> members)
		{
			foreach (var member in members)
			{
				if (member.IsAnnotated)
					continue;

				if (member.IsProperty ? ClassifyProperty(member.Type, member.Member) != null : Classify(member).Any())
					continue;

				if (_rules.Any(r => r.Type.IsMatch(member.Type) && r.Member.IsMatch(member.Member) && r.Parameter.IsMatch(member.Parameter)))
					continue;

				yield return member;
			}
		}

		static readonly Type _callSites = typeof(SqlTextUserCatalogGenerator).Assembly.GetType("LinqToDB.Analyzers.SqlTextCallSites", throwOnError: true)!;

		static IEnumerable<(string Kind, string Shape)> Classify(TextMember member)
		{
			var method = _callSites.GetMethod("Classify", BindingFlags.Static | BindingFlags.NonPublic)!;

			return (IEnumerable<(string, string)>)method.Invoke(null, [member.Type, member.Member, member.Parameter, member.IsHintMethod, true])!;
		}

		static string? ClassifyProperty(string type, string property)
		{
			return (string?)_callSites.GetMethod("ClassifyProperty", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [type, property]);
		}

		#region Inputs

		static IEnumerable<TextMember> EnumerateReflected()
		{
			foreach (var type in typeof(Sql).Assembly.GetExportedTypes())
			{
				if (type.IsDefined(typeof(CompilerGeneratedAttribute)))
					continue;

				var typeName = DisplayName(type);

				foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
					.Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)))
				{
					if (method.IsSpecialName && !method.IsConstructor && !method.Name.StartsWith("set_", StringComparison.Ordinal))
						continue;

					var isHint = method
						.GetCustomAttributesData()
						.Any(static a => a.AttributeType.FullName == "LinqToDB.Sql+QueryExtensionAttribute");

					foreach (var parameter in method.GetParameters())
					{
						if (parameter.Name == null || parameter.IsOut || !IsTextParameter(parameter.ParameterType, parameter.Name))
							continue;

						var annotated = parameter
							.GetCustomAttributesData()
							.Any(static a => a.AttributeType.FullName == "LinqToDB.Remote.SqlTextAttribute");
						var name      = method.IsConstructor ? ".ctor" : method.Name;

						if (method.Name.StartsWith("set_", StringComparison.Ordinal))
						{
							var propertyName = method.Name.Substring(4);
							var property     = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
							var isAnnotated  = property
								?.GetCustomAttributesData()
								.Any(static a => a.AttributeType.FullName == "LinqToDB.Remote.SqlTextAttribute") == true;

							yield return new(typeName, propertyName, "value", false, isAnnotated, IsProperty: true);
						}
						else
						{
							yield return new(typeName, name, parameter.Name, isHint, annotated, IsProperty: false);
						}
					}
				}
			}
		}

		static readonly Regex _apiLine = new(@"^(?:static |override |virtual |abstract )*(?<member>LinqToDB\.[\w.`]+?)\.(?<name>[\w.]+|\.ctor)\((?<params>[^)]*)\)", RegexOptions.Compiled);
		static readonly Regex _apiParam = new(@"(?<type>[\w.`<>?!\[\],]+) (?<name>\w+)(?: = [^,]+)?$", RegexOptions.Compiled);

		// Members a TFM other than net8.0 declares: absent from the reflected net8.0 assembly, so only the PublicAPI text sees them.
		static IEnumerable<TextMember> EnumerateTfmOnly()
		{
			var root = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "Source", "LinqToDB", "PublicAPI");

			foreach (var tfm in new[] { "net462", "netstandard2.0", "net9.0", "net10.0" })
			{
				foreach (var file in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" })
				{
					var path = Path.Combine(root, tfm, file);

					if (!File.Exists(path))
						continue;

					foreach (var line in File.ReadLines(path))
					{
						var match = _apiLine.Match(line);

						if (!match.Success)
							continue;

						foreach (var part in SplitParameters(match.Groups["params"].Value))
						{
							var text  = part.Trim();
							var param = _apiParam.Match(text);

							if (text.StartsWith("out ", StringComparison.Ordinal) || !param.Success || !IsTextTypeName(param.Groups["type"].Value, param.Groups["name"].Value))
								continue;

							yield return new(match.Groups["member"].Value, match.Groups["name"].Value, param.Groups["name"].Value, false, false, false);
						}
					}
				}
			}
		}

		static IEnumerable<string> SplitParameters(string parameters)
		{
			var depth = 0;
			var start = 0;

			for (var i = 0; i < parameters.Length; i++)
			{
				switch (parameters[i])
				{
					case '<': depth++; break;
					case '>': depth--; break;
					case ',' when depth == 0:
						yield return parameters.Substring(start, i - start);
						start = i + 1;
						break;
				}
			}

			if (start < parameters.Length)
				yield return parameters.Substring(start);
		}

		static bool IsTextParameter(Type type, string name)
		{
			type = type.IsByRef ? type.GetElementType()! : type;

			if (type == typeof(string) || type == typeof(string[]) || type == typeof(FormattableString) || type == typeof(RawSqlString))
				return true;

			if (type.IsGenericType && type.GetGenericArguments() is [var element] && element == typeof(string)
				&& type.GetGenericTypeDefinition() is var definition
				&& (definition == typeof(IEnumerable<>) || definition == typeof(IList<>) || definition == typeof(List<>) || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>)))
			{
				return true;
			}

			// object and generic parameters reach a text key when the value is a string
			return (type == typeof(object) || type == typeof(object[]) || type.IsGenericParameter) && _validatorKeys.Contains(name);
		}

		static bool IsTextTypeName(string type, string name)
		{
			type = type.TrimEnd('?', '!');

			return type is "string" or "string[]" or "string![]" or "string?[]" or "System.FormattableString" or "LinqToDB.RawSqlString"
				|| Regex.IsMatch(type, @"^System\.Collections\.Generic\.(IEnumerable|IList|List|ICollection|IReadOnlyList)<string[?!]?>$")
				|| ((type is "object" or "object[]" or "object?[]" || Regex.IsMatch(type, @"^T\w*$")) && _validatorKeys.Contains(name));
		}

		static string DisplayName(Type type)
		{
			return Regex
				.Replace(type.FullName ?? type.Name, @"`\d+", "")
				.Replace('+', '.');
		}

		static string ThisFile([CallerFilePath] string path = "") => path;

		#endregion
	}
}
