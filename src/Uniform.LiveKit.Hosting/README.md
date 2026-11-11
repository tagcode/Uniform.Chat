# Uniform.LiveKit.Hosting

Runs the Uniform.LiveKit.Authorization service in an ASP.NET host:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddLiveKitAuthorization(); // section LiveKit
WebApplication app = builder.Build();
app.MapLiveKitAuthorization("/livekit/jwt"); // /sfu/get, /get_token, /healthz
await app.RunAsync();
```
