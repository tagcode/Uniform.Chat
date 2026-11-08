namespace Uniform.Stun.Protocol;

/// <summary>Error codes of <see href="https://www.rfc-editor.org/rfc/rfc8489#section-14.8">STUN</see> and <see href="https://www.rfc-editor.org/rfc/rfc8656#section-19">TURN</see>, with their reason phrases in <see cref="Reason"/>.</summary>
public static class StunErrorCodes
{
    /// <summary>Use another server, given in ALTERNATE-SERVER.</summary>
    public const int TryAlternate = 300;
    /// <summary>The request was malformed.</summary>
    public const int BadRequest = 400;
    /// <summary>Credentials are missing or wrong; the response has a realm and a nonce.</summary>
    public const int Unauthorized = 401;
    /// <summary>The request is not allowed, e.g. a peer address the server does not relay to.</summary>
    public const int Forbidden = 403;
    /// <summary>The request has comprehension-required attributes the server does not know.</summary>
    public const int UnknownAttribute = 420;
    /// <summary>The client already has an allocation, or has none for a request that needs one.</summary>
    public const int AllocationMismatch = 437;
    /// <summary>The nonce has expired; the response has a new one.</summary>
    public const int StaleNonce = 438;
    /// <summary>The server does not support the requested address family.</summary>
    public const int AddressFamilyNotSupported = 440;
    /// <summary>The credentials differ from those the allocation was made with.</summary>
    public const int WrongCredentials = 441;
    /// <summary>The server does not relay over the requested transport.</summary>
    public const int UnsupportedTransportProtocol = 442;
    /// <summary>The peer's address family differs from the relayed address's.</summary>
    public const int PeerAddressFamilyMismatch = 443;
    /// <summary>The user has as many allocations as allowed.</summary>
    public const int AllocationQuotaReached = 486;
    /// <summary>A temporary failure of the server.</summary>
    public const int ServerError = 500;
    /// <summary>The server cannot take more allocations now.</summary>
    public const int InsufficientCapacity = 508;

    /// <summary>Return the reason phrase of a code, as the RFCs name it.</summary>
    public static string Reason(int code) => code switch
    {
        TryAlternate => "Try Alternate",
        BadRequest => "Bad Request",
        Unauthorized => "Unauthorized",
        Forbidden => "Forbidden",
        UnknownAttribute => "Unknown Attribute",
        AllocationMismatch => "Allocation Mismatch",
        StaleNonce => "Stale Nonce",
        AddressFamilyNotSupported => "Address Family not Supported",
        WrongCredentials => "Wrong Credentials",
        UnsupportedTransportProtocol => "Unsupported Transport Protocol",
        PeerAddressFamilyMismatch => "Peer Address Family Mismatch",
        AllocationQuotaReached => "Allocation Quota Reached",
        ServerError => "Server Error",
        InsufficientCapacity => "Insufficient Capacity",
        // Return a generic phrase for others
        _ => "Error",
    };

    /// <summary>Return the value of an ERROR-CODE attribute: class and number, then the reason phrase.</summary>
    public static byte[] Encode(int code, string? reason = null)
    {
        // Two zero bytes, the hundreds, the rest, then UTF-8 text
        byte[] text = System.Text.Encoding.UTF8.GetBytes(reason ?? Reason(code));
        byte[] value = new byte[4 + text.Length];
        value[2] = (byte)(code / 100);
        value[3] = (byte)(code % 100);
        text.CopyTo(value, 4);
        // Return value
        return value;
    }
}
