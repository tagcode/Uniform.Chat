---
uid: Uniform.LiveKit.Hosting.Article
title: Uniform.LiveKit.Hosting
---
# Uniform.LiveKit.Hosting

[`AddLiveKitAuthorization`](xref:Uniform.LiveKit.Hosting.LiveKitHostingExtensions.AddLiveKitAuthorization*) binds [`LiveKitOptions`](xref:Uniform.LiveKit.Hosting.LiveKitOptions) to section `LiveKit`, and lists it among the settings with [`AddOptionSection`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddOptionSection*). It registers a [`LiveKitAuthorizer`](xref:Uniform.LiveKit.Authorization.LiveKitAuthorizer) that reads the current settings at each request, so changes need no restart. The API secret is marked secret. It also registers a [`FederationOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.FederationOpenIdVerifier), unless the host registered another [`IOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.IOpenIdVerifier) first. Requests go through the named HTTP client [`HttpClientName`](xref:Uniform.LiveKit.Hosting.LiveKitHostingExtensions.HttpClientName), which tests can point at fakes.

[`MapLiveKitAuthorization`](xref:Uniform.LiveKit.Hosting.LiveKitHostingExtensions.MapLiveKitAuthorization*) serves the endpoints of lk-jwt-service under a prefix:

| Endpoint | Answer |
|---|---|
| `POST /get_token`, `POST /sfu/get` | `{url, jwt}`, or a Matrix error with its status |
| `OPTIONS` of those | 200 with CORS headers, for Element Call in a browser |
| Other methods of those | 405 |
| `GET /healthz`, `HEAD /healthz` | 200 |

While `LiveKit:Enabled` is false, the endpoints answer 404 `M_UNRECOGNIZED`.

[Uniform.Chat.Server](xref:Uniform.Chat.Server.Article) embeds it at `/livekit/jwt`, and [Uniform.LiveKit.Server](xref:Uniform.LiveKit.Server.Article) runs it alone.
