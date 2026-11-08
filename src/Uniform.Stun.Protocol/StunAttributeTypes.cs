namespace Uniform.Stun.Protocol;

/// <summary>Attribute types of <see href="https://www.rfc-editor.org/rfc/rfc8489#section-18.3">STUN</see>, <see href="https://www.rfc-editor.org/rfc/rfc8656#section-18">TURN</see> and <see href="https://www.rfc-editor.org/rfc/rfc8445#section-16.1">ICE</see>.</summary>
/// <remarks>Types below 0x8000 are comprehension-required: a receiver that does not know one must refuse the message, see <see cref="IsComprehensionRequired"/>.</remarks>
public static class StunAttributeTypes
{
    /// <summary>Address the server saw, in the clear; older servers.</summary>
    public const ushort MappedAddress = 0x0001;
    /// <summary>User name, e.g. ICE's <c>remote:local</c> fragments.</summary>
    public const ushort Username = 0x0006;
    /// <summary>HMAC-SHA1 over the message.</summary>
    public const ushort MessageIntegrity = 0x0008;
    /// <summary>Error code and reason.</summary>
    public const ushort ErrorCode = 0x0009;
    /// <summary>Comprehension-required attribute types the receiver did not know, in a 420 response.</summary>
    public const ushort UnknownAttributes = 0x000A;
    /// <summary>TURN channel number.</summary>
    public const ushort ChannelNumber = 0x000C;
    /// <summary>TURN allocation lifetime in seconds.</summary>
    public const ushort Lifetime = 0x000D;
    /// <summary>TURN peer address, XOR'ed.</summary>
    public const ushort XorPeerAddress = 0x0012;
    /// <summary>TURN data.</summary>
    public const ushort Data = 0x0013;
    /// <summary>Realm of long-term credentials.</summary>
    public const ushort Realm = 0x0014;
    /// <summary>Nonce of long-term credentials.</summary>
    public const ushort Nonce = 0x0015;
    /// <summary>TURN relayed address, XOR'ed.</summary>
    public const ushort XorRelayedAddress = 0x0016;
    /// <summary>TURN address family of the relayed address the client wants.</summary>
    public const ushort RequestedAddressFamily = 0x0017;
    /// <summary>TURN request for an even relay port.</summary>
    public const ushort EvenPort = 0x0018;
    /// <summary>TURN requested transport, 17 for UDP.</summary>
    public const ushort RequestedTransport = 0x0019;
    /// <summary>TURN request to set the don't-fragment bit towards peers.</summary>
    public const ushort DontFragment = 0x001A;
    /// <summary>HMAC-SHA256 over the message.</summary>
    public const ushort MessageIntegritySha256 = 0x001C;
    /// <summary>The password algorithm the client chose.</summary>
    public const ushort PasswordAlgorithm = 0x001D;
    /// <summary>Hash of the user name, instead of <see cref="Username"/>.</summary>
    public const ushort Userhash = 0x001E;
    /// <summary>Address the server saw, XOR'ed with the magic cookie and transaction.</summary>
    public const ushort XorMappedAddress = 0x0020;
    /// <summary>TURN token of a port reserved by an earlier allocation.</summary>
    public const ushort ReservationToken = 0x0022;
    /// <summary>ICE candidate priority.</summary>
    public const ushort Priority = 0x0024;
    /// <summary>ICE nomination by the controlling side.</summary>
    public const ushort UseCandidate = 0x0025;
    /// <summary>TURN request for a second relayed address of the other family.</summary>
    public const ushort AdditionalAddressFamily = 0x8000;
    /// <summary>TURN error of the additional address family.</summary>
    public const ushort AddressErrorCode = 0x8001;
    /// <summary>Password algorithms the server offers.</summary>
    public const ushort PasswordAlgorithms = 0x8002;
    /// <summary>Another server to try.</summary>
    public const ushort AlternateServer = 0x8023;
    /// <summary>Software name.</summary>
    public const ushort Software = 0x8022;
    /// <summary>CRC-32 over the message.</summary>
    public const ushort Fingerprint = 0x8028;
    /// <summary>ICE: the sender is controlled, with its tie breaker.</summary>
    public const ushort IceControlled = 0x8029;
    /// <summary>ICE: the sender is controlling, with its tie breaker.</summary>
    public const ushort IceControlling = 0x802A;

    /// <summary>Return whether an attribute type is comprehension-required: below 0x8000.</summary>
    public static bool IsComprehensionRequired(ushort type) => type < 0x8000;
}
