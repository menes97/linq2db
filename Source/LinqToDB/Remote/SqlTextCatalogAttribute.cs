using System;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Names the type that supplies the SQL text catalog of an assembly.
	/// </summary>
	[AttributeUsage(AttributeTargets.Assembly)]
	public sealed class SqlTextCatalogAttribute : Attribute
	{
		/// <summary>Creates the attribute.</summary>
		/// <param name="catalogType">Type implementing <see cref="ISqlTextCatalog"/> with a parameterless constructor.</param>
		public SqlTextCatalogAttribute(Type catalogType)
		{
			CatalogType = catalogType;
		}

		/// <summary>Gets the type implementing <see cref="ISqlTextCatalog"/>.</summary>
		public Type CatalogType { get; }
	}
}
