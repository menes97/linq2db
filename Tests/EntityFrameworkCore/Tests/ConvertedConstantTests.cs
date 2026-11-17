using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using NUnit.Framework;

using Shouldly;

using Tests;

namespace LinqToDB.EntityFrameworkCore.Tests
{
	[TestFixture]
	public sealed class ConvertedConstantTests : ContextTestBase<ConvertedConstantContext>
	{
		// ' ends a literal on any database; \' does not end it on MySQL, so only an escaper that also doubles \ keeps the rest inside
		static readonly Uri _injectedAddress = new ("http://x/?q=\\' OR 1=1 -- x");

		protected override ConvertedConstantContext CreateProviderContext(string provider, DbContextOptions<ConvertedConstantContext> options)
		{
			return new ConvertedConstantContext(options);
		}

		protected override DbContextOptionsBuilder<ConvertedConstantContext> ProviderSetup(string provider, string connectionString, DbContextOptionsBuilder<ConvertedConstantContext> optionsBuilder, bool useNodaTime)
		{
			return base.ProviderSetup(provider, connectionString, optionsBuilder, useNodaTime)
				.ReplaceService<IValueConverterSelector, ProfileKeyConverterSelector>();
		}

		ConvertedConstantContext CreateSeededContext(string provider)
		{
			var ctx = CreateContext(provider);

			using var db = ctx.CreateLinqToDBConnection();
			using var _ = new DisableBaseline("setup db");

			db.GetTable<Profile>().Delete();
			db.Insert(new Profile { TenantId = 1, Name = "one",   Homepage = _injectedAddress,           ParentKey = new ProfileKey(7) });
			db.Insert(new Profile { TenantId = 1, Name = "two",   Homepage = new Uri("http://a/"),       ParentKey = null              });
			db.Insert(new Profile { TenantId = 2, Name = "three", Homepage = new Uri("http://b/"),       ParentKey = new ProfileKey(8) });

			return ctx;
		}

		[Test]
		public void ConvertedConstantInListIsWrittenByTheProviderEscaper([EFDataSources] string provider)
		{
			using var ctx = CreateSeededContext(provider);

			// new[] { p.Homepage, p.Blog }.Contains(<constant>), the shape Dynamic LINQ builds for "@1 in (Homepage, Blog)"
			var p        = Expression.Parameter(typeof(Profile), "p");
			var columns  = Expression.NewArrayInit(typeof(Uri), Expression.Property(p, nameof(Profile.Homepage)), Expression.Property(p, nameof(Profile.Blog)));
			var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Uri)], columns, Expression.Constant(_injectedAddress, typeof(Uri)));
			var tenant   = Expression.Equal(Expression.Property(p, nameof(Profile.TenantId)), Expression.Constant(1));
			var filter   = Expression.Lambda<Func<Profile, bool>>(Expression.AndAlso(tenant, contains), p);

			var names = ctx.Profiles.ToLinqToDB().Where(filter).Select(x => x.Name).ToArray();

			names.ShouldBe(["one"]);
		}

		[Test]
		public void ConvertedConstantInConditionIsTranslated([EFDataSources] string provider)
		{
			using var ctx = CreateSeededContext(provider);

			var p      = Expression.Parameter(typeof(Profile), "p");
			var choice = Expression.Condition(
				Expression.Equal(Expression.Property(p, nameof(Profile.Name)), Expression.Constant("two")),
				Expression.Constant(null, typeof(Uri)),
				Expression.Property(p, nameof(Profile.Homepage)));
			var filter = Expression.Lambda<Func<Profile, bool>>(Expression.Equal(choice, Expression.Constant(new Uri("http://b/"), typeof(Uri))), p);

			var names = ctx.Profiles.ToLinqToDB().Where(filter).Select(x => x.Name).ToArray();

			names.ShouldBe(["three"]);
		}

		[Test]
		public void NullConvertedStructIsNotPassedToTheConverter([EFDataSources] string provider)
		{
			using var ctx = CreateSeededContext(provider);

			var withoutParent = ctx.Profiles.ToLinqToDB().Where(x => x.ParentKey == null).Select(x => x.Name).ToArray();
			var withParent    = ctx.Profiles.ToLinqToDB().Where(x => x.ParentKey.HasValue).Select(x => x.Name).OrderBy(x => x).ToArray();

			withoutParent.ShouldBe(["two"]);
			withParent.ShouldBe(["one", "three"]);
		}

		[Test]
		public void NullConvertedStructConstantInProjectionIsRead([EFDataSources] string provider)
		{
			using var ctx = CreateSeededContext(provider);

			var keys = ctx.Profiles.ToLinqToDB()
				.OrderBy(x => x.Id)
				.Select(x => x.Name == "one" ? (ProfileKey?)null : x.ParentKey)
				.ToArray();

			keys.ShouldBe([null, null, new ProfileKey(8)]);
		}
	}

	public sealed class ConvertedConstantContext : DbContext
	{
		public ConvertedConstantContext(DbContextOptions options) : base(options)
		{
		}

		public DbSet<Profile> Profiles { get; set; } = null!;
	}

	public sealed class Profile
	{
		public int         Id        { get; set; }
		public int         TenantId  { get; set; }
		public string      Name      { get; set; } = null!;
		public Uri?        Homepage  { get; set; }
		public Uri?        Blog      { get; set; }
		public ProfileKey? ParentKey { get; set; }
	}

	public readonly record struct ProfileKey(int Value);

	// rejects default(ProfileKey) as validated strongly typed ids do
	public sealed class ProfileKeyConverter : ValueConverter<ProfileKey, int>
	{
		public ProfileKeyConverter(ConverterMappingHints? mappingHints = null)
			: base(key => ToProvider(key), value => new ProfileKey(value), mappingHints)
		{
		}

		static int ToProvider(ProfileKey key) => key.Value != 0 ? key.Value : throw new InvalidOperationException("default ProfileKey");
	}

	public sealed class ProfileKeyConverterSelector : ValueConverterSelector
	{
		public ProfileKeyConverterSelector(ValueConverterSelectorDependencies dependencies) : base(dependencies)
		{
		}

		public override IEnumerable<ValueConverterInfo> Select(Type modelClrType, Type? providerClrType = null)
		{
			foreach (var converter in base.Select(modelClrType, providerClrType))
				yield return converter;

			if ((Nullable.GetUnderlyingType(modelClrType) ?? modelClrType) != typeof(ProfileKey))
				yield break;

			if (providerClrType != null && (Nullable.GetUnderlyingType(providerClrType) ?? providerClrType) != typeof(int))
				yield break;

			yield return new ValueConverterInfo(typeof(ProfileKey), typeof(int), i => new ProfileKeyConverter(i.MappingHints));
		}
	}
}
