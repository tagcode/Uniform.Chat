---
uid: Uniform.MatrixRtc.Hosting.Article
title: Uniform.MatrixRtc.Hosting
---
# Uniform.MatrixRtc.Hosting

[`AddLiveKitAuthorization`](xref:Uniform.MatrixRtc.Hosting.LiveKitHostingExtensions.AddLiveKitAuthorization*) binds [`LiveKitOptions`](xref:Uniform.MatrixRtc.Hosting.LiveKitOptions) to section `MatrixRtc`, and lists it among the settings with [`AddOptionSection`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddOptionSection*). It registers a [`LiveKitAuthorizer`](xref:Uniform.MatrixRtc.Authorization.LiveKitAuthorizer) that reads the current settings at each request, so changes need no restart. Settings still under the section's earlier name, `LiveKit`, are read where `MatrixRtc` does not set them, and a warning at the first request names them to rename. The API secret is marked secret. It also registers a [`FederationOpenIdVerifier`](xref:Uniform.MatrixRtc.Authorization.FederationOpenIdVerifier), unless the host registered another [`IOpenIdVerifier`](xref:Uniform.MatrixRtc.Authorization.IOpenIdVerifier) first. Requests go through the named HTTP client [`HttpClientName`](xref:Uniform.MatrixRtc.Hosting.LiveKitHostingExtensions.HttpClientName), which tests can point at fakes.

[`MapLiveKitAuthorization`](xref:Uniform.MatrixRtc.Hosting.LiveKitHostingExtensions.MapLiveKitAuthorization*) serves the endpoints of lk-jwt-service under a prefix:

| Endpoint | Answer |
|---|---|
| `POST /get_token`, `POST /sfu/get` | `{url, jwt}`, or a Matrix error with its status |
| `OPTIONS` of those | 200 with CORS headers, for Element Call in a browser |
| Other methods of those | 405 |
| `GET /healthz`, `HEAD /healthz` | 200 |

While `MatrixRtc:Enabled` is false, the endpoints answer 404 `M_UNRECOGNIZED`.

[Uniform.Chat.Server](xref:Uniform.Chat.Server.Article) embeds it at `/livekit/jwt`, and [Uniform.MatrixRtc.Server](xref:Uniform.MatrixRtc.Server.Article) runs it alone.
