# Uniform.MatrixRtc.Hosting

Runs the Uniform.MatrixRtc.Authorization service in an ASP.NET host:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddMatrixRtcAuthorization(); // section MatrixRtc
WebApplication app = builder.Build();
app.MapMatrixRtcAuthorization("/livekit/jwt"); // /sfu/get, /get_token, /healthz
await app.RunAsync();
```
