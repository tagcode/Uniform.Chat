# Uniform.Matrix.Server

A Matrix homeserver engine on ASP.NET Core and SQLite: accounts and devices, rooms, sync, profiles, typing and receipts, the room directory, media with thumbnails, and the end-to-end encryption relay with cross-signing and key backup. Federation with other servers is not there yet.

```bash
dotnet add package Uniform.Matrix.Server
```

## Host it

```csharp
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddSettings();
builder.AddMatrixServer();
WebApplication app = builder.Build();
app.MapMatrixServer();
await app.RunAsync();
```

`AddMatrixServer` registers the options, the database and the services; `MapMatrixServer` adds the Matrix error handling and CORS, and maps the Client-Server API endpoints. The database is created and migrated at start.

## Settings

| Option | Default | Meaning |
|---|---|---|
| `Server:ServerName` | `localhost` | The name in user IDs and room aliases; choose it before creating accounts |
| `Server:PublicBaseUrl` | not set | Address announced at `/.well-known/matrix/client` |
| `Server:Registration` | `Disabled` | `Disabled`, `Open` or `Token` |
| `Server:RegistrationTokens` | not set | Comma-separated tokens for `Token` registration |
| `Storage:DataDirectory` | the app's data directory | Database, signing key, media and logs |
| `Media:MaxUploadSize` | 50 MiB | Largest upload |

`AdminService` creates accounts, resets passwords and manages the room directory without an access token, for an operator's own tools.

The server speaks plain HTTP; put a reverse proxy in front of it for HTTPS. The [Durum server](https://github.com/tagcode/Uniform.Matrix) is a ready application with a terminal dashboard, built on this package.

More in [Server](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/server.md).

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [Architecture](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/architecture.md)
- [Building and testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/building-and-testing.md)
