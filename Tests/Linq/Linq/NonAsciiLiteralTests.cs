using System.Linq;

using LinqToDB;
using LinqToDB.Mapping;

using NUnit.Framework;

using Shouldly;

namespace Tests.Linq
{
	/// <summary>
	/// Sybase and Oracle write text with a character above ASCII as an escaped literal (u&amp;'...' / UNISTR), so the command
	/// text stays ASCII whatever the connection charset. The value must still round-trip and compare as a text literal does.
	/// </summary>
	[TestFixture]
	public class NonAsciiLiteralTests : TestBase
	{
		// look-alike apostrophes (U+FF07, U+2019), a backslash, an apostrophe and a character outside the BMP
		const string Text = "MARK ÄΩ＇’ \\ ' \U0001F600 end";

		[Table]
		sealed class TextRow
		{
			[PrimaryKey]                                       public int     Id        { get; set; }
			[Column(DataType = DataType.NChar,    Length = 40)] public string? FixedN    { get; set; }
			[Column(DataType = DataType.NVarChar, Length = 40)] public string? VariableN { get; set; }
		}

		[Test]
		public void InlinedTextRoundTrips([IncludeDataSources(TestProvName.AllSybase, TestProvName.AllOracle)] string context)
		{
			using var db = GetDataContext(context);

			db.Select(() => Sql.ToSql(Text)).ShouldBe(Text);
			db.Select(() => Sql.ToSql('＇')).ShouldBe('＇');
		}

		[Test]
		public void InlinedTextMatchesStoredValues([IncludeDataSources(TestProvName.AllSybase, TestProvName.AllOracle)] string context)
		{
			using var db    = GetDataContext(context);
			using var table = db.CreateLocalTable<TextRow>();

			const string shortText = "ÄΩ x";

			table.Insert(() => new TextRow { Id = 1, FixedN = Text, VariableN = Text });
			table.Insert(() => new TextRow { Id = 2, FixedN = shortText, VariableN = shortText });

			table.Count(r => r.FixedN    == Sql.ToSql(Text)).ShouldBe(1);
			table.Count(r => r.VariableN == Sql.ToSql(Text)).ShouldBe(1);
			table.Count(r => r.FixedN    == Sql.ToSql(shortText)).ShouldBe(1);
			table.Count(r => r.VariableN == Sql.ToSql(shortText)).ShouldBe(1);
			table.Where(r => r.Id == 1).Select(r => Sql.ToSql(Text)).Single().ShouldBe(Text);
		}
	}
}
