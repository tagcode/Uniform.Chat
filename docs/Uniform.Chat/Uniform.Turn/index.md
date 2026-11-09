---
uid: Uniform.Turn.Article
title: Uniform.Turn
---
# Uniform.Turn

A STUN and TURN server: [`TurnServer`](xref:Uniform.Turn.TurnServer) answers Binding requests and relays UDP for authenticated clients that reach it over UDP, TCP or TLS, following [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656). [Uniform.Stun.Hosting](xref:Uniform.Stun.Hosting.Article) runs it in a .NET host.

## Requests

| Method | Does |
|---|---|
| Binding | Answers the client's address, without credentials |
| Allocate | A relay socket on a random free port of the relay range, for UDP over IPv4; a retransmission gets the same answer, another request on the same 5-tuple 437 |
| Refresh | A new lifetime, from the default to the maximum; zero ends the allocation |
| CreatePermission | Lets the peers' addresses through for five minutes, all or none |
| ChannelBind | A channel for a peer for ten minutes; a channel has one peer and a peer one channel |
| Send indication, channel data | Data to a permitted peer |

Data from peers goes to the client on the peer's channel when bound, else in a Data indication; without a permission it is dropped. Over TCP and TLS, the server frames the stream with [`ChannelData.FrameLength`](xref:Uniform.Stun.Protocol.ChannelData.FrameLength*) and pads channel data; the allocation ends with the connection.

## Credentials

Requests other than Binding need [long-term credentials](https://www.rfc-editor.org/rfc/rfc8489#section-9.2). An [`ITurnAuthenticator`](xref:Uniform.Turn.ITurnAuthenticator) gives the keys a user may have: [`SharedSecretAuthenticator`](xref:Uniform.Turn.SharedSecretAuthenticator) for time-limited credentials of shared secrets, [`StaticUserAuthenticator`](xref:Uniform.Turn.StaticUserAuthenticator) for fixed users, and [`CombinedAuthenticator`](xref:Uniform.Turn.CombinedAuthenticator) for several. A request is accepted when its MESSAGE-INTEGRITY matches any key.

Nonces are stateless: the time they were made and an HMAC of it with the client's address, under a key made at each start. They are stale after [`NonceLifetime`](xref:Uniform.Turn.TurnServerOptions.NonceLifetime), and a restart makes all stale, which clients answer by retrying with the new nonce.

## Safety

- [`DeniedPeers`](xref:Uniform.Turn.TurnServerOptions.DeniedPeers), by default [`IpRange.NonPublic`](xref:Uniform.Turn.IpRange.NonPublic), are refused with 403, so the server cannot be used to reach its own network; [`AllowedPeers`](xref:Uniform.Turn.TurnServerOptions.AllowedPeers) make exceptions. The server's own relayed addresses are always allowed.
- Quotas per user name and in total (486 and 508), and lifetimes; a sweep every five seconds removes what has expired.
- Requests with comprehension-required attributes it does not know get 420, e.g. EVEN-PORT and RESERVATION-TOKEN, which it does not implement.

## Several networks

UDP replies must leave from the address their request came to, or the client drops them. A socket on 0.0.0.0 leaves the choice to routing, which on a computer with several networks may pick another, e.g. the default route of a wired network while forwarded traffic arrives over WiFi. With [`ListenAddress`](xref:Uniform.Turn.TurnServerOptions.ListenAddress) 0.0.0.0, the server therefore binds a UDP socket on each IPv4 address of the interfaces that are up at start, and each relay socket on the address its client reached. TCP connections keep their addresses, so TCP listens on 0.0.0.0. Addresses that appear after start need a restart.

## Relaying inside the server

Data from one allocation to another relayed address of the same server goes from one to the other in memory, without leaving the server. Two clients behind the same router as the server, e.g. at home, then need no NAT hairpinning, which many routers lack.

## Not implemented

TCP relaying ([RFC 6062](https://www.rfc-editor.org/rfc/rfc6062)), IPv6 relayed addresses, EVEN-PORT and reservations, the SHA-256 integrity of RFC 8489, and DTLS as a client transport. Browsers and the apps of this project use none of them for calls.
