---
title: Errors
uid: Uniform.Matrix.Protocol.Client.Errors.Article
---
# Errors

```csharp
try
{
    await client.JoinRoomAsync("#missing:matrix.org");
}
catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
{
    Console.Error.WriteLine("No such room");
}
catch (MatrixException ex) when (ex.Errcode == ErrorCodes.Forbidden)
{
    Console.Error.WriteLine($"Not allowed: {ex.Error}");
}
```

[MatrixException](xref:Uniform.Matrix.Protocol.Core.MatrixException) is thrown when the server returns a Matrix error body. Rate-limited requests are retried automatically before an exception is thrown, unless the server asks to wait longer than [`MaxRateLimitDelay`](xref:Uniform.Matrix.Protocol.Client.MatrixClient.MaxRateLimitDelay) (30 seconds); then `ex.RetryAfter` says how long to wait. Other failures, such as an HTML error page from a proxy, throw [`HttpRequestException`](https://learn.microsoft.com/dotnet/api/system.net.http.httprequestexception) with the HTTP status code.
