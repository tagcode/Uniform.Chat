# Uniform.Stun

STUN and TURN clients in pure C#, on a UDP socket the caller owns and reads:

- `StunClient`: STUN transactions with the retransmissions of RFC 8489, e.g. Binding requests to learn the public address.
- `TurnClient`: allocates a relayed address on a TURN server, permits peers, binds channels, relays data both ways, and refreshes everything until disposed.

The messages are in Uniform.Stun.Protocol. Uniform.Matrix.WebRtc uses these for ICE.
