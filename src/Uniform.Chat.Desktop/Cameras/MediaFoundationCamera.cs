namespace Uniform.Chat.Desktop.Cameras;

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Video;
using static Uniform.Chat.Desktop.Video.MediaFoundation;

/// <summary>A Windows webcam through <see href="https://learn.microsoft.com/windows/win32/medfound/media-foundation-programming-guide">Media Foundation</see>'s source reader: NV12 pictures of 640x480, converted to I420 and encoded by <see cref="EncodingCamera"/>; half size when the peer asks for less.</summary>
/// <remarks>The calls and COM interfaces are those of <see cref="Video.MediaFoundation"/>. Windows' camera privacy setting decides whether the app may open the camera.</remarks>
public sealed class MediaFoundationCamera : EncodingCamera
{
    private static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VideoCaptureSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid MediaSourceId = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    private static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01"), SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private IMFActivate[] devices = [];
    private IMFSourceReader? reader;
    private CancellationTokenSource? stopping;
    private Thread? capturing;
    private int index;
    private string? wanted;
    private int width;
    private int height;

    /// <summary>Whether another camera is attached.</summary>
    public override bool CanSwitch => devices.Length > 1;

    /// <summary>Return whether this machine has a video capture device; false where Media Foundation is missing.</summary>
    public static bool Available()
    {
        // Windows with at least one camera
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            IMFActivate[] found = Enumerate();
            foreach (IMFActivate device in found) Release(device);
            return found.Length > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // On no Media Foundation, as on Windows N without its media pack
            return false;
        }
    }

    /// <summary>The cameras by their symbolic links, which stay the same across runs, named by their friendly names.</summary>
    public override IReadOnlyList<CameraChoice> Cameras
    {
        get
        {
            // The open list, else a fresh one freed after
            IMFActivate[] listed = devices.Length > 0 ? devices : Enumerate();
            List<CameraChoice> choices = [.. listed.Select((d, i) => new CameraChoice(DeviceString(d, SymbolicLink) ?? $"camera{i}", DeviceString(d, FriendlyName) ?? $"Camera {i + 1}"))];
            if (listed != devices) foreach (IMFActivate device in listed) Release(device);
            // Return choices
            return choices;
        }
    }

    /// <summary>The symbolic link of the camera capturing, or of the one to start with.</summary>
    public override string? CameraId => reader is not null && index < devices.Length ? DeviceString(devices[index], SymbolicLink) : wanted;

    /// <summary>Start with, or switch to, the camera of symbolic link <paramref name="id"/>; the current one stays when it does not open.</summary>
    public override void SelectCamera(string id)
    {
        // Before the start, the one to open
        if (reader is null)
        {
            wanted = id;
            return;
        }
        int number = Array.FindIndex(devices, d => DeviceString(d, SymbolicLink) == id);
        if (number < 0 || number == index) return;
        int previous = index;
        Stop();
        if (Open(number)) index = number;
        else if (Open(previous)) index = previous;
        // Done
        if (reader is not null) Run();
    }

    /// <summary>Open the wanted camera, else the first that opens, and start capturing on a thread of its own.</summary>
    public override void Start()
    {
        // The cameras, the wanted one first, then the first that opens
        devices = Enumerate();
        int chosen = wanted is null ? -1 : Array.FindIndex(devices, d => DeviceString(d, SymbolicLink) == wanted);
        if (chosen >= 0 && Open(chosen)) index = chosen;
        else for (index = 0; index < devices.Length && !Open(index); index++) { }
        if (reader is null) throw new IOException("No camera opened; Windows' camera privacy setting may not allow it.");
        // Done
        Run();
    }

    /// <summary>Open the next camera.</summary>
    public override void SwitchCamera()
    {
        // The others in turn
        if (devices.Length < 2) return;
        Stop();
        for (int i = 1; i <= devices.Length; i++)
        {
            if (!Open((index + i) % devices.Length)) continue;
            index = (index + i) % devices.Length;
            break;
        }
        // Done
        if (reader is not null) Run();
    }

    /// <summary>Stop capturing and release the cameras.</summary>
    public override ValueTask DisposeAsync()
    {
        // The reader, then the devices
        Stop();
        foreach (IMFActivate device in devices) Release(device);
        devices = [];
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Return a string attribute of a device, e.g. its friendly name; null when it has none.</summary>
    private static string? DeviceString(IMFActivate device, Guid key)
    {
        // Allocated by Media Foundation, freed here
        if (device.GetAllocatedString(key, out IntPtr value, out uint length) < 0 || value == IntPtr.Zero) return null;
        string text = Marshal.PtrToStringUni(value, (int)length);
        Marshal.FreeCoTaskMem(value);
        // Return text
        return text;
    }

    /// <summary>Return the video capture devices.</summary>
    private static IMFActivate[] Enumerate()
    {
        // Media Foundation, then the devices of the video capture type
        if (!IsAvailable) throw new COMException("Media Foundation does not start.");
        Check(MFCreateAttributes(out IMFAttributes attributes, 1));
        Check(attributes.SetGUID(SourceType, VideoCaptureSource));
        Check(MFEnumDeviceSources(attributes, out IntPtr array, out uint count));
        Release(attributes);

        // Each pointer wrapped, the array freed
        IMFActivate[] found = new IMFActivate[count];
        for (int i = 0; i < count; i++)
        {
            IntPtr device = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            found[i] = (IMFActivate)Wrappers.GetOrCreateObjectForComInstance(device, CreateObjectFlags.UniqueInstance);
            Marshal.Release(device);
        }
        Marshal.FreeCoTaskMem(array);
        // Return devices
        return found;
    }

    /// <summary>Open camera <paramref name="number"/> for NV12 at 640x480, converted by the reader where the camera gives another format; return whether it opened.</summary>
    private bool Open(int number)
    {
        // The camera's media source, and a reader that converts
        try
        {
            Check(devices[number].ActivateObject(MediaSourceId, out IntPtr source));
            Check(MFCreateAttributes(out IMFAttributes attributes, 1));
            Check(attributes.SetUINT32(EnableVideoProcessing, 1));
            int created = MFCreateSourceReaderFromMediaSource(source, attributes, out IMFSourceReader opened);
            Marshal.Release(source);
            Release(attributes);
            Check(created);

            // NV12 at 640x480, then the size it took
            Check(MFCreateMediaType(out IMFMediaType type));
            Check(type.SetGUID(MajorType, Video));
            Check(type.SetGUID(Subtype, Nv12));
            Check(type.SetUINT64(FrameSize, 640UL << 32 | 480));
            Check(opened.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type));
            Release(type);
            Check(opened.GetCurrentMediaType(FirstVideoStream, out IMFMediaType current));
            Check(current.GetUINT64(FrameSize, out ulong size));
            Release(current);
            width = (int)(size >> 32);
            height = (int)(size & 0xFFFFFFFF);
            reader = opened;
            return true;
        }
        catch (COMException)
        {
            // On a camera in use, or one the privacy setting withholds
            return false;
        }
    }

    /// <summary>Start the capture thread.</summary>
    private void Run()
    {
        // Until stopped
        stopping = new CancellationTokenSource();
        CancellationToken token = stopping.Token;
        IMFSourceReader source = reader!;
        capturing = new Thread(() => Capture(source, token)) { IsBackground = true, Name = "camera" };
        capturing.Start();
    }

    /// <summary>Read samples until stopped: each copied out of its buffer, then paced, converted and encoded by the base.</summary>
    private void Capture(IMFSourceReader source, CancellationToken token)
    {
        // Samples block until a picture is ready
        byte[] nv12 = [];
        while (!token.IsCancellationRequested)
        {
            if (source.ReadSample(FirstVideoStream, 0, out _, out uint flags, out _, out IMFSample? sample) < 0 || (flags & EndOfStream) != 0) break;
            if (sample is null) continue;

            // The picture's bytes, contiguous
            try
            {
                if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) < 0) continue;
                if (buffer.Lock(out IntPtr data, out _, out uint length) >= 0)
                {
                    if (nv12.Length != length) nv12 = new byte[length];
                    Marshal.Copy(data, nv12, 0, (int)length);
                    buffer.Unlock();
                }
                Release(buffer);
            }
            finally
            {
                Release(sample);
            }
            byte[] captured = nv12;
            // Done
            Deliver(() => ToI420(captured, width, height, Low));
        }
    }

    /// <summary>Return NV12, a luma plane and then interleaved U and V, as I420; with <paramref name="half"/>, every other pixel and row, at half the size.</summary>
    internal static VideoFrame ToI420(byte[] nv12, int width, int height, bool half)
    {
        // Sizes of the result; the luma rows are the width apart
        int step = half ? 2 : 1;
        int w = width / step, h = height / step, cw = (w + 1) / 2, ch = (h + 1) / 2;
        byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
        int chroma = width * height;
        for (int row = 0; row < h; row++)
        {
            int source = row * step * width;
            for (int column = 0; column < w; column++) y[row * w + column] = nv12[source + column * step];
        }

        // Chroma pairs of every kept chroma row and column
        for (int row = 0; row < ch; row++)
        {
            int source = chroma + row * step * width;
            for (int column = 0; column < cw; column++)
            {
                int pair = source + column * step * 2;
                if (pair + 1 >= nv12.Length) break;
                u[row * cw + column] = nv12[pair];
                v[row * cw + column] = nv12[pair + 1];
            }
        }
        // Return picture
        return new VideoFrame(w, h, y, u, v);
    }

    /// <summary>Stop the thread and release the reader.</summary>
    private void Stop()
    {
        // The thread ends after its sample
        stopping?.Cancel();
        capturing?.Join(TimeSpan.FromSeconds(2));
        capturing = null;
        if (reader is not null) Release(reader);
        reader = null;
        // Done
        if (index < devices.Length) devices[index].ShutdownObject();
    }
}
