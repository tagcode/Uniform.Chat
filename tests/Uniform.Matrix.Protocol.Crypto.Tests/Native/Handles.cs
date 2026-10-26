namespace Uniform.Matrix.Protocol.Crypto.Tests.Native;

using System.Runtime.InteropServices;

/// <summary>Owns a vodozemac account pointer.</summary>
internal sealed class AccountHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the account.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_account_free(handle);
        // Done
        return true;
    }
}

/// <summary>Owns a vodozemac Olm session pointer.</summary>
internal sealed class SessionHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the session.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_session_free(handle);
        // Done
        return true;
    }
}

/// <summary>Owns a vodozemac outbound Megolm session pointer.</summary>
internal sealed class GroupSessionHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the session.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_group_session_free(handle);
        // Done
        return true;
    }
}

/// <summary>Owns a vodozemac inbound Megolm session pointer.</summary>
internal sealed class InboundGroupSessionHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the session.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_inbound_group_session_free(handle);
        // Done
        return true;
    }
}

/// <summary>Owns a vodozemac SAS pointer until it is established.</summary>
internal sealed class SasHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the SAS.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_sas_free(handle);
        // Done
        return true;
    }
}

/// <summary>Owns a vodozemac established SAS pointer.</summary>
internal sealed class EstablishedSasHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <summary>Whether the pointer is null.</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;
    /// <summary>Free the established SAS.</summary>
    protected override bool ReleaseHandle()
    {
        // Free natively
        NativeMethods.vz_established_sas_free(handle);
        // Done
        return true;
    }
}
