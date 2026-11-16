# LINQ to DB Remote Data Context Over Signal/R<!-- omit in toc -->

[![License](https://img.shields.io/github/license/linq2db/linq2db)](MIT-LICENSE.txt)

## About

This package provides required server classes to query database from remote client using [Linq To DB](https://github.com/linq2db/linq2db) library over Signal/R transport.

You can find working example [here](https://github.com/linq2db/linq2db/tree/master/Examples\Remote\SignalR).

## Security

A remote client sends the server a finished query, and the server renders it as SQL. By default `LinqService` treats its clients as **untrusted** (`TrustedClient = false`) and rejects a request that carries anything the server cannot vouch for:

- **SQL text** must be text linq2db itself produces, text from a catalog in one of the `MappingAssemblies` or `SqlTextAssemblies`, or text the host adds with `LinqService.SqlText.Add(kind, text)`. Raw SQL (`FromSql`, `Sql.Expr`), free-text hints, hint helpers that build text with a number (`SqlServerHints.Query.Fast(10)`), and table, schema or database name overrides are rejected unless the host adds the exact text.
- **Types** must be scalars known to the server's mapping schema, or come from an assembly in `LinqService.MappingAssemblies`. `LinqService<T>` adds the assembly of `T`. Mappings that exist only on the client are rejected.
- **Connection configurations** must be listed in `LinqService.AllowedConfigurations`. When the list is empty, only the default configuration is accepted.

A rejected request fails with `RemoteRequestRejectedException`. Its `Rejections` list gives the reason, kind, text and position of each violation. The message repeats only the client's own text. Override `LinqService.HandleException` to log rejections on the server. To review what the server accepts, `LinqService.DumpSqlText(configuration)` lists the effective catalog. `SqlTextCatalog.Export` and `Load` save and restore host additions and removals. The catalog's contents are not a public contract and change between releases.

This policy limits **what SQL** a client can run. It is not authorization: a permitted query can read any mapped table. Authenticate and authorize clients in the transport, and connect with a database login that has only the rights clients need. Set `TrustedClient = true` only when every client is as trusted as the server.

Clients and server should use the same linq2db version. Text a newer client generates may be rejected by an older server until the server is updated.

### Known limitations

- Text built at run time and composed `Sql.Ext` chains cannot be catalogued; clients sending them are rejected unless the host adds the exact text.
- Hint helpers that take a number or a client value (`FIRST_ROWS(n)`, `PARALLEL(n)`, `MAX_EXECUTION_TIME(n)`, `SET_VAR`, `OPT_PARAM`, ClickHouse `SETTINGS`), and query-block names such as `@qb`, are rejected the same way.
- Text with a list of items, such as the columns of `GROUPING SETS`, is accepted for up to 8 items.
- A member mapped through an explicit interface implementation has a dotted name, so it is rejected as a `MERGE` source field.
- The `string` to `XDocument` conversion now prohibits DTD processing. This applies to every connection, trusted or not, and to direct connections too.

### Generating the catalog

linq2db can generate the SQL text catalog of your own assembly at build time, so you don't have to hand-write one for your mappings and queries.

**Opting in.** Set the property in the project that holds your mappings and queries:

```xml
<PropertyGroup>
  <LinqToDBSqlTextCatalog>true</LinqToDBSqlTextCatalog>
</PropertyGroup>
```

The generator ships in the `linq2db.Analyzers` package, which comes with `linq2db`. It runs for C# projects only, during builds: its diagnostics appear in build output, not as live editor squiggles. `EnableLinqToDBAnalyzers=false` turns it off along with the other linq2db rules.

**What it catalogs.** Constant SQL text in the project:

- the text of `Sql.Expression`, `Sql.Function`, `Sql.Property`, `Sql.Extension`, `Sql.TableFunction`, `Sql.TableExpression` and `Sql.QueryExtension` attributes, including ones created with `new` for fluent mapping;
- constant arguments your own translators and extension builders pass to linq2db parameters marked `[SqlText]`;
- constant text at the raw-SQL call sites:
  - `FromSql` and `Sql.Expr`, as interpolated strings or `RawSqlString`;
  - table, query and join hints, and `With`;
  - `TableName`, `SchemaName`, `DatabaseName`, `ServerName` and the table-name parameters and options;
  - the `CreateTable` statement header and footer, and the `Merge` hint;
  - `QueryHints` and `NextQueryHints`;
- `DbType` text from `[Column]`, `[DataType]`, `HasDbType` and `DbDataType`.

**Allow and deny files.** Add entries the generator cannot find, or remove ones you don't want, with a file in the `SqlTextCatalog.Export` / `Load` format:

```xml
<ItemGroup>
  <LinqToDBSqlText Include="sqltext.txt" />
</ItemGroup>
```

A `+` line allows a text; a `-` line removes it from every source, linq2db's own catalog included. Files are read only from under the project directory, or from under a directory listed in `LinqToDBSqlTextRoots`, and never from `obj/`.

**Diagnostics.**

| Rule | Meaning |
|---|---|
| L2DB1005 | text at a catalogued site is not a compile-time constant |
| L2DB1006 | text with a format item inside quotes or a comment; never catalogued |
| L2DB1007 | malformed allow or deny line |
| L2DB1008 | allow or deny file outside the project directory, or under `obj/`; ignored |
| L2DB1009 | the referenced linq2db cannot hold a generated catalog |
| L2DB1010 | a deny line that cannot be applied (file outside the allowed directories, unreadable, or not carried by the referenced linq2db); an error, because a lost denial widens what clients may send |

**Getting it to the server.** Register the assembly in `LinqService.SqlTextAssemblies`, or export its catalog with `SqlTextCatalog.Export(writer, assembly)` and load it on the server with `LinqService.SqlText.Load`. Don't add an assembly to `MappingAssemblies` just to get its catalog: that also admits its types.

## Other Transports

We provide
[gRPC support](https://www.nuget.org/packages/linq2db.Remote.gRPC),
[Signal/R Client support](https://www.nuget.org/packages/linq2db.Remote.SignalR.Client) and
[Signal/R Server support](https://www.nuget.org/packages/linq2db.Remote.SignalR.Server),
[HttpClient.Client support](https://www.nuget.org/packages/linq2db.Remote.HttpClient.Client) and
[HttpClient.Server support](https://www.nuget.org/packages/linq2db.Remote.HttpClient.Server),
and [WCF support](https://www.nuget.org/packages/linq2db.Remote.Wcf) (.NET Framework only currently).

If you need Remote Context support over other types of transport, you can create [feature request](https://github.com/linq2db/linq2db/issues/new) or send PR with transport implementation.
