using System;
using System.Data.Linq;
using System.Globalization;
using System.Text;

using LinqToDB.Internal.Common;
using LinqToDB.Internal.Mapping;
using LinqToDB.SqlQuery;

namespace LinqToDB.Internal.DataProvider.Sybase
{
	public sealed class SybaseMappingSchema : LockedMappingSchema
	{
#if SUPPORTS_COMPOSITE_FORMAT
		private static readonly CompositeFormat TIME3_FORMAT    = CompositeFormat.Parse("'{0:hh\\:mm\\:ss\\.fff}'");
		private static readonly CompositeFormat DATETIME_FORMAT = CompositeFormat.Parse("'{0:yyyy-MM-dd HH:mm:ss.fff}'");
#else
		private const string TIME3_FORMAT    = "'{0:hh\\:mm\\:ss\\.fff}'";
		private const string DATETIME_FORMAT = "'{0:yyyy-MM-dd HH:mm:ss.fff}'";
#endif

		SybaseMappingSchema() : base(ProviderName.Sybase)
		{
			SetValueToSqlConverter(typeof(string)  ,       (sb, _,_,v) => ConvertStringToSql  (sb, (string)v));
			SetValueToSqlConverter(typeof(char)    ,       (sb, _,_,v) => ConvertCharToSql    (sb, (char)v));
			SetValueToSqlConverter(typeof(TimeSpan),       (sb,dt,_,v) => ConvertTimeSpanToSql(sb, dt, (TimeSpan)v));
			SetValueToSqlConverter(typeof(byte[])  ,       (sb, _,_,v) => ConvertBinaryToSql  (sb, (byte[])v));
			SetValueToSqlConverter(typeof(Binary)  ,       (sb, _,_,v) => ConvertBinaryToSql  (sb, ((Binary)v).ToArray()));
			SetValueToSqlConverter(typeof(DateTime),       (sb,_,_, v) => BuildDateTime(sb, (DateTime)v));
			SetValueToSqlConverter(typeof(DateTimeOffset), (sb,_,_, v) => BuildDateTime(sb, ((DateTimeOffset)v).DateTime));

			SetDataType(typeof(string),  new SqlDataType(DataType.NVarChar, typeof(string), 255));
			// in ASE DECIMAL=DECIMAL(18,0)
			SetDataType(typeof(decimal), new SqlDataType(DataType.Decimal,  typeof(decimal), 18, 10));

			SetDefaultValue(typeof(DateTime), new DateTime(1753, 1, 1));
		}

		static void BuildDateTime(StringBuilder stringBuilder, DateTime value)
		{
			stringBuilder.AppendFormat(CultureInfo.InvariantCulture, DATETIME_FORMAT, value);
		}

		static void ConvertBinaryToSql(StringBuilder stringBuilder, byte[] value)
		{
			stringBuilder
				.Append("0x")
				.AppendByteArrayAsHexViaLookup32(value);
		}

		static void ConvertTimeSpanToSql(StringBuilder stringBuilder, SqlDataType sqlDataType, TimeSpan value)
		{
			if (sqlDataType.Type.DataType == DataType.Int64)
			{
				stringBuilder.Append(value.Ticks.ToString(NumberFormatInfo.InvariantInfo));
			}
			else
			{
				// to match logic for values as parameters
				if (value < TimeSpan.Zero)
					value = TimeSpan.FromDays(1 - value.Days) + value;

				stringBuilder.AppendFormat(CultureInfo.InvariantCulture, TIME3_FORMAT, value);
			}
		}

		static readonly Action<StringBuilder, int> AppendConversionAction = AppendConversion;

		static void AppendConversion(StringBuilder stringBuilder, int value)
		{
			stringBuilder.Append(CultureInfo.InvariantCulture, $"char({value})");
		}

		// On an 8-bit server charset the driver maps a character it cannot encode to a look-alike, and U+FF07 or U+2019 can
		// become the apostrophe that ends the literal. Text with a character above ASCII is therefore written as u&'...' with
		// that character escaped, so the command text stays ASCII; ASCII text is written as before.
		static void ConvertStringToSql(StringBuilder stringBuilder, string value)
		{
			if (IsAscii(value))
				DataTools.ConvertStringToSql(stringBuilder, "+", null, AppendConversionAction, value, null);
			else
				DataTools.ConvertStringToSql(stringBuilder, "+", "u&", AppendConversionAction, EscapeUnicode(value), null);
		}

		static void ConvertCharToSql(StringBuilder stringBuilder, char value)
		{
			if (value < 0x80)
				DataTools.ConvertCharToSql(stringBuilder, "'", AppendConversionAction, value);
			else
				stringBuilder.Append("u&'").Append(EscapeUnicode(value.ToString())).Append('\'');
		}

		static bool IsAscii(string value)
		{
			foreach (var c in value)
				if (c >= 0x80)
					return false;

			return true;
		}

		// the u& escapes: \xxxx for a BMP character, \+yyyyyy for a surrogate pair, \\ for the backslash
		static string EscapeUnicode(string value)
		{
			var sb = new StringBuilder(value.Length * 2);

			for (var i = 0; i < value.Length; i++)
			{
				var c = value[i];

				if (c == '\\')
					sb.Append("\\\\");
				else if (c < 0x80)
					sb.Append(c);
				else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
					sb.Append("\\+").Append(char.ConvertToUtf32(c, value[++i]).ToString("x6", CultureInfo.InvariantCulture));
				else
					sb.Append('\\').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
			}

			return sb.ToString();
		}

		internal static readonly SybaseMappingSchema Instance = new ();

		public sealed class NativeMappingSchema() : LockedMappingSchema(ProviderName.Sybase, Instance);

		public sealed class ManagedMappingSchema() : LockedMappingSchema(ProviderName.SybaseManaged, Instance);
	}
}
