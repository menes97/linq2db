using System.Collections.Generic;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Declares the argument roles of the extension builders an assembly defines, so a <see cref="LinqService"/> accepts their
	/// hints from untrusted clients. Implement it on a type named by <see cref="SqlTextCatalogAttribute"/>, alone or beside
	/// <see cref="ISqlTextCatalog"/>.
	/// </summary>
	/// <remarks>
	/// Roles are read from the active provider's assembly and from <see cref="LinqService.MappingAssemblies"/>, never from
	/// <see cref="LinqService.SqlTextAssemblies"/>. A role applies only to a builder declared in the same assembly, and never
	/// replaces the roles of a builder the library declares. An argument key of the builder with no declared role is rejected.
	/// Declare a role from what the builder does with the value: a wrong role widens what clients may send.
	/// </remarks>
	public interface ISqlExtensionBuilderRoles
	{
		/// <summary>Gets the roles of every argument key of the assembly's extension builders.</summary>
		IReadOnlyList<SqlExtensionArgument> Arguments { get; }
	}
}
