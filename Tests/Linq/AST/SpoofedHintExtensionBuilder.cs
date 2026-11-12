using System.Text;

using LinqToDB.Internal.SqlQuery;

// Same namespace and name as the library's internal HintExtensionBuilder, so a validator keyed by type name alone would
// apply the library builder's argument roles to it.
namespace LinqToDB.Internal.SqlProvider
{
	public sealed class HintExtensionBuilder : ISqlQueryExtensionBuilder
	{
		public void Build(NullabilityContext nullability, ISqlBuilder sqlBuilder, StringBuilder stringBuilder, SqlQueryExtension sqlQueryExtension)
		{
			stringBuilder.Append("MARKER");
		}
	}
}
