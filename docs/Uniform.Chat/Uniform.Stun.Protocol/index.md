---
uid: Uniform.Stun.Protocol.Article
title: Uniform.Stun.Protocol
---
# Uniform.Stun.Protocol

STUN and TURN messages without sockets, shared by the clients of [Uniform.Stun](xref:Uniform.Stun.Article), the server of [Uniform.Turn](xref:Uniform.Turn.Article), and the homeserver, which hands out TURN credentials.

| Type | What | Specification |
|---|---|---|
| [`StunMessage`](xref:Uniform.Stun.Protocol.StunMessage) | Parse and encode messages; MESSAGE-INTEGRITY, FINGERPRINT and long-term keys | [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489) |
| [`StunAttributeTypes`](xref:Uniform.Stun.Protocol.StunAttributeTypes), [`StunErrorCodes`](xref:Uniform.Stun.Protocol.StunErrorCodes) | Attribute types and error codes of STUN, TURN and ICE | [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489), [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656), [RFC 8445](https://www.rfc-editor.org/rfc/rfc8445) |
| [`StunAddress`](xref:Uniform.Stun.Protocol.StunAddress) | XOR'ed address attributes | [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489#section-14.2) |
| [`ChannelData`](xref:Uniform.Stun.Protocol.ChannelData) | TURN channel data, and framing of STUN and channel data on TCP and TLS streams | [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656#section-12.4) |
| [`TurnCredentials`](xref:Uniform.Stun.Protocol.TurnCredentials) | Time-limited credentials from a shared secret, as homeservers and coturn's `use-auth-secret` use | [TURN REST API draft](https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00) |

The cryptography is .NET's own: HMAC-SHA1 for integrity, MD5 for long-term keys, and a table-driven CRC-32 for fingerprints.
