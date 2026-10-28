# Storage

EF Core on SQLite, `ServerDbContext` with entities in `Storage/Entities.cs`. `DatabaseMigrator` applies migrations at start, before the server answers. Each schema change adds a migration:

```bash
dotnet tool run dotnet-ef migrations add <Name> --project src/Uniform.Matrix.Server --output-dir Storage/Migrations
```

Services take an `IDbContextFactory<ServerDbContext>` and use a short-lived context per operation; they are singletons.
