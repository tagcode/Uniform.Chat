---
title: MatrixRTC authorization server
uid: Uniform.LiveKit.Server.Article
---
# Uniform LiveKit Authorization Server

`uniform-livekit-server` lets Element X, Element Call and other MatrixRTC apps join calls on your LiveKit server. An app proves who its user is with a Matrix OpenID token, and gets back a LiveKit token for the call's room. It replaces lk-jwt-service and reads the same environment variables. Uniform Chat Server has the same service built in.

- [Running](xref:Uniform.LiveKit.Server.Running.Article): first start, and as a service.
- [Settings](xref:Uniform.LiveKit.Server.Settings.Article): every setting.
- [Homeservers](xref:Uniform.LiveKit.Server.Homeservers.Article): announcing it from Synapse or Uniform Chat Server.
