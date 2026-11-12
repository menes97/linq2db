using System;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Names a type that supplies SQL text catalog entries of an assembly. An assembly may declare several, for example
	/// a generated catalog and a hand-written one.
	/// </summary>
	[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
	public sealed class SqlTextCatalogAttribute : Attribute
	{
		/// <summary>Creates the attribute.</summary>
		/// <param name="catalogType">
		/// Type implementing <see cref="ISqlTextCatalog"/>, <see cref="ISqlExtensionBuilderRoles"/> or both, with a parameterless constructor.
		/// </param>
		public SqlTextCatalogAttribute(Type catalogType)
		{
			CatalogType = catalogType;
		}

		/// <summary>Gets the type implementing <see cref="ISqlTextCatalog"/>, <see cref="ISqlExtensionBuilderRoles"/> or both.</summary>
		public Type CatalogType { get; }
	}
}
