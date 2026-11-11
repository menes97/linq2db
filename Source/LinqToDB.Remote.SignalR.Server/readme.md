# LINQ to DB Remote Data Context Over Signal/R<!-- omit in toc -->

[![License](https://img.shields.io/github/license/linq2db/linq2db)](MIT-LICENSE.txt)

## About

This package provides required server classes to query database from remote client using [Linq To DB](https://github.com/linq2db/linq2db) library over Signal/R transport.

You can find working example [here](https://github.com/linq2db/linq2db/tree/master/Examples\Remote\SignalR).

## Security

A remote client sends the server a finished query, and the server renders it as SQL. By default `LinqService` treats its clients as **untrusted** (`TrustedClient = false`) and rejects a request that carries anything the server cannot vouch for:

- **SQL text** must be text linq2db itself produces, text from a catalog in one of the `MappingAssemblies`, or text the host adds with `LinqService.SqlText.Add(kind, text)`. Raw SQL (`FromSql`, `Sql.Expr`), free-text hints, hint helpers that build text with a number (`SqlServerHints.Query.Fast(10)`), and table, schema or database name overrides are rejected unless the host adds the exact text.
- **Types** must be scalars known to the server's mapping schema, or come from an assembly in `LinqService.MappingAssemblies`. `LinqService<T>` adds the assembly of `T`. Mappings that exist only on the client are rejected.
- **Connection configurations** must be listed in `LinqService.AllowedConfigurations`. When the list is empty, only the default configuration is accepted.

A rejected request fails with `RemoteRequestRejectedException`. Its `Rejections` list gives the reason, kind, text and position of each violation. The message repeats only the client's own text. Override `LinqService.HandleException` to log rejections on the server. To review what the server accepts, `LinqService.DumpSqlText(configuration)` lists the effective catalog. `SqlTextCatalog.Export` and `Load` save and restore host additions and removals. The catalog's contents are not a public contract and change between releases.

This policy limits **what SQL** a client can run. It is not authorization: a permitted query can read any mapped table. Authenticate and authorize clients in the transport, and connect with a database login that has only the rights clients need. Set `TrustedClient = true` only when every client is as trusted as the server.

Clients and server should use the same linq2db version. Text a newer client generates may be rejected by an older server until the server is updated.

## Other Transports

We provide
[gRPC support](https://www.nuget.org/packages/linq2db.Remote.gRPC),
[Signal/R Client support](https://www.nuget.org/packages/linq2db.Remote.SignalR.Client) and
[Signal/R Server support](https://www.nuget.org/packages/linq2db.Remote.SignalR.Server),
[HttpClient.Client support](https://www.nuget.org/packages/linq2db.Remote.HttpClient.Client) and
[HttpClient.Server support](https://www.nuget.org/packages/linq2db.Remote.HttpClient.Server),
and [WCF support](https://www.nuget.org/packages/linq2db.Remote.Wcf) (.NET Framework only currently).

If you need Remote Context support over other types of transport, you can create [feature request](https://github.com/linq2db/linq2db/issues/new) or send PR with transport implementation.
