using System.Diagnostics.CodeAnalysis;

namespace LinqToDB.Internal.DataProvider
{
	public abstract class IdentifierServiceBase :  IIdentifierService
	{
		public abstract bool   IsFit(IdentifierKind identifierKind, string identifier, [NotNullWhen(false)] out int? sizeDecrement);

		public virtual string CorrectAlias(string alias) => CorrectAliasDefault(alias);

		// also applied by RemoteQueryValidator to an untrusted client's aliases, so they render as a direct query's do
		internal static string CorrectAliasDefault(string alias)
		{
			alias = alias.TrimStart('_');

			var cs      = alias.ToCharArray();
			var replace = false;

			for (var i = 0; i < cs.Length; i++)
			{
				var c = cs[i];

				if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
					continue;

				cs[i]   = ' ';
				replace = true;
			}

			if (replace)
				alias = new string(cs).Replace(" ", "", System.StringComparison.Ordinal);

			return alias;
		}
	}
}
