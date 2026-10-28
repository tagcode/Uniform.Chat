---
title: Uniform.Matrix.Protocol.Client
uid: Uniform.Matrix.Protocol.Client.Article
---
# Uniform.Matrix.Protocol.Client

[MatrixClient](xref:Uniform.Matrix.Protocol.Client.MatrixClient) wraps one homeserver and one session. It is split into partial files by spec area. Each endpoint is one method that sends a request and returns a response record. All requests go through a single private `SendAsync`, which:

1. adds the bearer token for authenticated endpoints, and refuses to send if not logged in;
2. serializes the body with [MatrixJson.Options](xref:Uniform.Matrix.Protocol.Core.MatrixJson.Options);
3. retries [rate-limited](https://spec.matrix.org/latest/client-server-api/#rate-limiting) requests up to [MaxRateLimitRetries](xref:Uniform.Matrix.Protocol.Client.MatrixClient.MaxRateLimitRetries) times, waiting as long as the server asks, unless that is longer than [MaxRateLimitDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.MaxRateLimitDelay);
4. turns other non-2xx responses into a [MatrixException](xref:Uniform.Matrix.Protocol.Core.MatrixException) when the body is a [standard error](https://spec.matrix.org/latest/client-server-api/#standard-error-response), or an [`HttpRequestException`](https://learn.microsoft.com/dotnet/api/system.net.http.httprequestexception) otherwise.

Path segments such as room IDs (`!abc:server`), aliases (`#room:server`) and event IDs are always percent-escaped. The caller owns the [`HttpClient`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient), which keeps the library usable with [`IHttpClientFactory`](https://learn.microsoft.com/dotnet/api/system.net.http.ihttpclientfactory), custom handlers and test stubs.

## Session

After login, [Session](xref:Uniform.Matrix.Protocol.Client.MatrixClient.Session) holds a [MatrixSession](xref:Uniform.Matrix.Protocol.Client.MatrixSession) with homeserver, user ID, device ID and access token. Store it and pass it to the `MatrixClient(HttpClient, MatrixSession)` constructor to resume without logging in again.

## Sync

[SyncLoopAsync](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncLoopAsync*) is the main way to receive events. It long-polls [sync](https://spec.matrix.org/latest/client-server-api/#syncing), passes each `next_batch` as the next `since`, and yields every response. Transient failures (network errors, 5xx, timeouts, rate limiting) are retried with exponential backoff between [SyncRetryDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncRetryDelay) and [SyncRetryMaxDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncRetryMaxDelay); other errors, such as an expired token, end the loop with an exception.

## User-interactive auth

Some endpoints, such as deleting devices, answer the first request with 401 and a body listing accepted [auth flows](https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api) and a session ID. Such bodies have no `errcode`, so the request pipeline maps them to [UserInteractiveAuthException](xref:Uniform.Matrix.Protocol.Client.UserInteractiveAuthException) before ordinary Matrix errors. Methods that need it take a password and answer a `m.login.password` challenge themselves; without a password the exception reaches the caller, who can ask the user and call again.

## Discovery

[HomeserverDiscovery.DiscoverAsync](xref:Uniform.Matrix.Protocol.Client.HomeserverDiscovery.DiscoverAsync*) follows [server discovery](https://spec.matrix.org/latest/client-server-api/#server-discovery): it reads `https://{server}/.well-known/matrix/client` and falls back to `https://{server}/` when the file does not exist.
