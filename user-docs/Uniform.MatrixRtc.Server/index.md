---
title: MatrixRTC authorization server
uid: Uniform.MatrixRtc.Server.Article
---
# Uniform MatrixRTC Authorization Server

`uniform-matrixrtc-server` lets Element X, Element Call and other MatrixRTC apps join calls on your LiveKit server. An app proves who its user is with a Matrix OpenID token, and gets back a SFU token for the call's room. It replaces lk-jwt-service, with environment variables named like its own. Uniform Chat Server has the same service built in.

- [Running](xref:Uniform.MatrixRtc.Server.Running.Article): first start, and as a service.
- [Settings](xref:Uniform.MatrixRtc.Server.Settings.Article): every setting.
- [Homeservers](xref:Uniform.MatrixRtc.Server.Homeservers.Article): announcing it from Synapse or Uniform Chat Server.
