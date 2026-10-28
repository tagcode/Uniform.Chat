---
title: Uniform.Matrix.Server
uid: Uniform.Matrix.Server.Article
---
# Uniform.Matrix.Server

The homeserver is three projects:

| Project | Role |
|---|---|
| [`Uniform.Matrix.Protocol.Server`](xref:Uniform.Matrix.Protocol.Server.Article) | Pure protocol code, no storage: [room versions](https://spec.matrix.org/latest/rooms/) 10, 11 and 12, the event format, content hashes, reference-hash event IDs, signing, redaction and the [authorization rules](https://spec.matrix.org/latest/rooms/v11/#authorization-rules). |
| [`Uniform.Matrix.Server`](xref:Uniform.Matrix.Server.Article) | The engine: `builder.AddMatrixServer()` registers options, the database and services; `app.MapMatrixServer()` adds the middleware and maps the endpoints. |
| [`Uniform.Matrix.Server.Host`](xref:Uniform.Matrix.Server.Host.Article) | The application: reads settings, sets up logging, and runs with the Terminal.Gui dashboard or without it (`Host:Ui`). |

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddSettings();
builder.AddMatrixServer();
WebApplication app = builder.Build();
app.MapMatrixServer();
await app.RunAsync();
```

## Not yet covered

Room upgrades, search, partial-state joins of large rooms, email pushers (stored but not sent), and PostgreSQL.
