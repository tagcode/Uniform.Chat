namespace Uniform.Matrix.Server.Http;

using Microsoft.AspNetCore.Http;

/// <summary>The <c>r0</c> paths of the client and media APIs, which <see href="https://spec.matrix.org/v1.1/">spec v1.1</see> renamed to <c>v3</c> without changing them, and the unstable key backup paths before that; older clients still use them.</summary>
public static class LegacyPaths
{
    private static readonly (PathString Old, PathString New)[] Prefixes = [("/_matrix/client/r0", "/_matrix/client/v3"), ("/_matrix/media/r0", "/_matrix/media/v3"), ("/_matrix/client/unstable/room_keys", "/_matrix/client/v3/room_keys")];

    /// <summary>Return <paramref name="path"/> with an <c>r0</c> prefix replaced by <c>v3</c>; other paths unchanged.</summary>
    public static PathString Rewrite(PathString path)
    {
        // First matching prefix
        foreach ((PathString old, PathString replacement) in Prefixes)
        {
            if (path.StartsWithSegments(old, out PathString rest)) return replacement.Add(rest);
        }
        // Return as it was
        return path;
    }
}
