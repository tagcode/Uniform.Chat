---
title: Uniform.Matrix.Server
uid: Uniform.Matrix.Server.Article
---
# Uniform.Matrix.Server

The homeserver is three projects:

| Project | Role |
|---|---|
| `Uniform.Matrix.Protocol.Server` | Pure protocol code, no storage: [room versions](https://spec.matrix.org/latest/rooms/) 10 and 11, the event format, content hashes, reference-hash event IDs, signing, redaction and the [authorization rules](https://spec.matrix.org/latest/rooms/v11/#authorization-rules). |
| `Uniform.Matrix.Server` | The engine: `builder.AddMatrixServer()` registers options, the database and services; `app.MapMatrixServer()` adds the middleware and maps the endpoints. |
| `Uniform.Matrix.Server.Host` | The application: reads settings, sets up logging, and runs with the Terminal.Gui dashboard or without it (`Host:Ui`). |

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddSettings();
builder.AddMatrixServer();
WebApplication app = builder.Build();
app.MapMatrixServer();
await app.RunAsync();
```

## Not yet covered

State resolution and checking received events against the state before them, room upgrades, threads, search, email pushers (stored but not sent), and PostgreSQL.
