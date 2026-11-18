# Uniform.MatrixRtc.Hosting

Runs the Uniform.MatrixRtc.Authorization service in an ASP.NET host:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddLiveKitAuthorization(); // section MatrixRtc
WebApplication app = builder.Build();
app.MapLiveKitAuthorization("/livekit/jwt"); // /sfu/get, /get_token, /healthz
await app.RunAsync();
```
