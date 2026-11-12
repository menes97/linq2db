using System;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Role of one argument key of an extension builder, declared through <see cref="ISqlExtensionBuilderRoles"/>.
	/// </summary>
	/// <param name="BuilderType">Extension builder type. It must be declared in the assembly that declares the roles.</param>
	/// <param name="Key">Argument key, or a pattern ending in <c>.*</c> for indexed keys such as <c>values.0</c>, <c>values.1</c>.</param>
	/// <param name="Role">How the builder renders the key's value.</param>
	public sealed record SqlExtensionArgument(Type BuilderType, string Key, SqlExtensionArgumentRole Role);
}
