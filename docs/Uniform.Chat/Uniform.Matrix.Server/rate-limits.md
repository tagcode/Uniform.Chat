---
title: Rate limits
uid: Uniform.Matrix.Server.RateLimits.Article
---
# Rate limits

[`RateLimits.AddMatrixRateLimits`](xref:Uniform.Matrix.Server.Http.RateLimits.AddMatrixRateLimits*) (in `Http/`) sets up ASP.NET Core's rate limiter with two token-bucket policies, which endpoints opt into with `RequireRateLimiting`: [`RateLimits.Writes`](xref:Uniform.Matrix.Server.Http.RateLimits.Writes) per access token, on room creation, invites, sending, state, redactions and uploads; and [`RateLimits.Logins`](xref:Uniform.Matrix.Server.Http.RateLimits.Logins) per client address, on `/login` and `/register`. A refused request gets 429 with `M_LIMIT_EXCEEDED`, `retry_after_ms` and a `Retry-After` header; [`MatrixClient`](xref:Uniform.Matrix.Protocol.Client.MatrixClient) waits that long and retries. The `RateLimits:*` settings are read at startup. Reads and sync are not limited. Client addresses come from the connection, as forwarded headers are not read yet, so behind a reverse proxy the login limits are shared by all users.
