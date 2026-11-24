namespace Uniform.Chat.Desktop.Video;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>The few calls of <see href="https://gstreamer.freedesktop.org/documentation/">GStreamer</see> 1.x that <see cref="GStreamerVideoSource"/> makes, through P/Invoke into the system's libraries, and whether they and the plugins to decode are installed.</summary>
[SupportedOSPlatform("linux")]
internal static partial class GStreamer
{
    private const string Core = "libgstreamer-1.0.so.0";
    private const string App = "libgstapp-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    /// <summary>States of an element.</summary>
    public const int StateNull = 1, StatePaused = 3, StatePlaying = 4;

    /// <summary>A state change that failed.</summary>
    public const int StateChangeFailure = 0;

    /// <summary>The time format of seeking and queries.</summary>
    public const int FormatTime = 3;

    /// <summary>Seek flags: flush what waits, and go to the exact time.</summary>
    public const int SeekFlushAccurate = 1 | 2;

    /// <summary>The error message type of the bus.</summary>
    public const int MessageError = 1 << 1;

    /// <summary>A clock time not set.</summary>
    public const ulong ClockTimeNone = ulong.MaxValue;

    /// <summary>Where a buffer's presentation time lies in its struct on 64-bit systems: after its mini object of 64 bytes and its pool pointer.</summary>
    public const int BufferPtsOffset = 72;

    /// <summary>Read access of a buffer map.</summary>
    public const int MapRead = 1;

    private static readonly Lazy<bool> available = new(Initialize);

    /// <summary>Whether GStreamer is installed with the elements video messages need.</summary>
    public static bool IsAvailable => available.Value;

    /// <summary>A buffer's memory, as gst_buffer_map fills it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MapInfo
    {
        public nint Memory;
        public int Flags;
        public nint Data;
        public nuint Size;
        public nuint MaxSize;
        public nint User0, User1, User2, User3;
        public nint Reserved0, Reserved1, Reserved2, Reserved3;
    }

    /// <summary>A GLib error.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    /// <summary>Load the libraries, initialize GStreamer, and check its elements; false when anything is missing.</summary>
    private static bool Initialize()
    {
        // The libraries, then the elements
        if (!NativeLibrary.TryLoad(Core, out _) || !NativeLibrary.TryLoad(App, out _)) return false;
        try
        {
            if (!gst_init_check(0, 0, out nint error)) return false;
            string[] elements = ["filesrc", "decodebin", "videoconvert", "videoflip", "audioconvert", "audioresample", "appsink"];
            foreach (string name in elements)
            {
                nint factory = gst_element_factory_find(name);
                if (factory == 0) return false;
                gst_object_unref(factory);
            }
            // Return found
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // On libraries of another version
            return false;
        }
    }

    /// <summary>Return the text of the first error on <paramref name="element"/>'s bus, or null for none.</summary>
    public static string? TakeError(nint element)
    {
        // The bus's error message, parsed
        nint bus = gst_element_get_bus(element);
        if (bus == 0) return null;
        try
        {
            nint message = gst_bus_pop_filtered(bus, MessageError);
            if (message == 0) return null;
            gst_message_parse_error(message, out nint error, out nint debug);
            string? text = error == 0 ? null : Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message);
            if (error != 0) g_error_free(error);
            if (debug != 0) g_free(debug);
            gst_mini_object_unref(message);
            // Return its text
            return text ?? "GStreamer failed.";
        }
        finally
        {
            gst_object_unref(bus);
        }
    }

    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool gst_init_check(nint argc, nint argv, out nint error);
    [DllImport(Core)] private static extern nint gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] public static extern nint gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out nint error);
    [DllImport(Core)] public static extern nint gst_bin_get_by_name(nint bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] public static extern void gst_util_set_object_arg(nint obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Core)] public static extern int gst_element_set_state(nint element, int state);
    [DllImport(Core)] public static extern int gst_element_get_state(nint element, out int state, out int pending, ulong timeout);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gst_element_seek_simple(nint element, int format, int flags, long position);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gst_element_query_duration(nint element, int format, out long duration);
    [DllImport(Core)] public static extern nint gst_element_get_bus(nint element);
    [DllImport(Core)] public static extern nint gst_bus_pop_filtered(nint bus, int types);
    [DllImport(Core)] public static extern void gst_message_parse_error(nint message, out nint error, out nint debug);
    [DllImport(Core)] public static extern void gst_object_unref(nint obj);
    [DllImport(Core)] public static extern void gst_mini_object_unref(nint obj);
    [DllImport(Core)] public static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(Core)] public static extern nint gst_sample_get_caps(nint sample);
    [DllImport(Core)] public static extern nint gst_sample_get_segment(nint sample);
    [DllImport(Core)] public static extern ulong gst_segment_to_stream_time(nint segment, int format, ulong position);
    [DllImport(Core)] public static extern nint gst_caps_get_structure(nint caps, uint index);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gst_structure_get_int(nint structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gst_buffer_map(nint buffer, out MapInfo info, int flags);
    [DllImport(Core)] public static extern void gst_buffer_unmap(nint buffer, ref MapInfo info);
    [DllImport(App)] public static extern nint gst_app_sink_try_pull_sample(nint sink, ulong timeout);
    [DllImport(App)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gst_app_sink_is_eos(nint sink);
    [DllImport(GLib)] private static extern void g_error_free(nint error);
    [DllImport(GLib)] private static extern void g_free(nint memory);
}
