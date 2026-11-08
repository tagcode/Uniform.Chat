# Uniform.Stun.Protocol

STUN and TURN messages in pure C#, without sockets:

- `StunMessage`: parse and encode messages of [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489) and [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656), with MESSAGE-INTEGRITY and FINGERPRINT, and long-term credential keys.
- `StunAttributeTypes`, `StunErrorCodes`: the attribute types and error codes of STUN, TURN and ICE.
- `StunAddress`: XOR'ed address attributes.
- `ChannelData`: TURN channel data, and framing of STUN and channel data on TCP and TLS streams.
- `TurnCredentials`: time-limited TURN credentials from a shared secret, as Matrix homeservers and coturn's `use-auth-secret` use.

The clients are in Uniform.Stun, the TURN server in Uniform.Turn.
