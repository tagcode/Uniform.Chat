namespace Uniform.Chat.Desktop.Cameras;

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Uniform.Matrix.Video;

/// <summary>A Windows webcam through <see href="https://learn.microsoft.com/windows/win32/medfound/media-foundation-programming-guide">Media Foundation</see>'s source reader: NV12 pictures of 640x480, converted to I420 and encoded by <see cref="EncodingCamera"/>; half size when the peer asks for less.</summary>
/// <remarks>The COM interfaces are source-generated and declare their methods in the Windows SDK headers' vtable order up to the last one used; slots before it that are never called keep placeholders. Windows' camera privacy setting decides whether the app may open the camera.</remarks>
public sealed partial class MediaFoundationCamera : EncodingCamera
{
    private const uint Version = 0x00020070, FirstVideoStream = 0xFFFFFFFC, EndOfStream = 2;
    private static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VideoCaptureSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), MediaSourceId = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static bool started;
    private IMFActivate[] devices = [];
    private IMFSourceReader? reader;
    private CancellationTokenSource? stopping;
    private Thread? capturing;
    private int index;
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

    /// <summary>Open the first camera and start capturing on a thread of its own.</summary>
    public override void Start()
    {
        // The cameras, then the first that opens
        devices = Enumerate();
        for (index = 0; index < devices.Length && !Open(index); index++) { }
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

    /// <summary>Return the video capture devices.</summary>
    private static IMFActivate[] Enumerate()
    {
        // Media Foundation once, then the devices of the video capture type
        if (!started) Check(MFStartup(Version, 0));
        started = true;
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

    /// <summary>Release a COM object now, rather than when collected.</summary>
    private static void Release(object instance)
    {
        // Wrappers of the source-generated interfaces
        if (instance is ComObject com) com.FinalRelease();
    }

    /// <summary>Throw for a failed HRESULT.</summary>
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    /// <summary>Media Foundation's start.</summary>
    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    /// <summary>A new attribute store.</summary>
    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    /// <summary>A new media type.</summary>
    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out IMFMediaType type);

    /// <summary>The devices of the type the attributes name; an array of activation objects to free.</summary>
    [LibraryImport("mf.dll")]
    private static partial int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activates, out uint count);

    /// <summary>A synchronous reader of a media source.</summary>
    [LibraryImport("mfreadwrite.dll")]
    private static partial int MFCreateSourceReaderFromMediaSource(IntPtr source, IMFAttributes attributes, out IMFSourceReader reader);

    /// <summary>IMFAttributes, up to SetGUID.</summary>
    [GeneratedComInterface]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    internal partial interface IMFAttributes
    {
        /// <summary>GetItem, unused.</summary>
        [PreserveSig] int GetItem();
        /// <summary>GetItemType, unused.</summary>
        [PreserveSig] int GetItemType();
        /// <summary>CompareItem, unused.</summary>
        [PreserveSig] int CompareItem();
        /// <summary>Compare, unused.</summary>
        [PreserveSig] int Compare();
        /// <summary>GetUINT32, unused.</summary>
        [PreserveSig] int GetUINT32();
        /// <summary>Return a 64-bit value.</summary>
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        /// <summary>GetDouble, unused.</summary>
        [PreserveSig] int GetDouble();
        /// <summary>GetGUID, unused.</summary>
        [PreserveSig] int GetGUID();
        /// <summary>GetStringLength, unused.</summary>
        [PreserveSig] int GetStringLength();
        /// <summary>GetString, unused.</summary>
        [PreserveSig] int GetString();
        /// <summary>GetAllocatedString, unused.</summary>
        [PreserveSig] int GetAllocatedString();
        /// <summary>GetBlobSize, unused.</summary>
        [PreserveSig] int GetBlobSize();
        /// <summary>GetBlob, unused.</summary>
        [PreserveSig] int GetBlob();
        /// <summary>GetAllocatedBlob, unused.</summary>
        [PreserveSig] int GetAllocatedBlob();
        /// <summary>GetUnknown, unused.</summary>
        [PreserveSig] int GetUnknown();
        /// <summary>SetItem, unused.</summary>
        [PreserveSig] int SetItem();
        /// <summary>DeleteItem, unused.</summary>
        [PreserveSig] int DeleteItem();
        /// <summary>DeleteAllItems, unused.</summary>
        [PreserveSig] int DeleteAllItems();
        /// <summary>Set a 32-bit value.</summary>
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        /// <summary>Set a 64-bit value.</summary>
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        /// <summary>SetDouble, unused.</summary>
        [PreserveSig] int SetDouble();
        /// <summary>Set a GUID value.</summary>
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        /// <summary>SetString, unused.</summary>
        [PreserveSig] int SetString();
        /// <summary>SetBlob, unused.</summary>
        [PreserveSig] int SetBlob();
        /// <summary>SetUnknown, unused.</summary>
        [PreserveSig] int SetUnknown();
        /// <summary>LockStore, unused.</summary>
        [PreserveSig] int LockStore();
        /// <summary>UnlockStore, unused.</summary>
        [PreserveSig] int UnlockStore();
        /// <summary>GetCount, unused.</summary>
        [PreserveSig] int GetCount();
        /// <summary>GetItemByIndex, unused.</summary>
        [PreserveSig] int GetItemByIndex();
        /// <summary>CopyAllItems, unused.</summary>
        [PreserveSig] int CopyAllItems();
    }

    /// <summary>IMFMediaType; only its attributes are used.</summary>
    [GeneratedComInterface]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    internal partial interface IMFMediaType : IMFAttributes;

    /// <summary>IMFActivate, up to ShutdownObject.</summary>
    [GeneratedComInterface]
    [Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
    internal partial interface IMFActivate : IMFAttributes
    {
        /// <summary>Create the object, e.g. the device's media source.</summary>
        [PreserveSig] int ActivateObject(in Guid riid, out IntPtr result);
        /// <summary>Shut the object down.</summary>
        [PreserveSig] int ShutdownObject();
    }

    /// <summary>IMFSourceReader, up to ReadSample.</summary>
    [GeneratedComInterface]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    internal partial interface IMFSourceReader
    {
        /// <summary>GetStreamSelection, unused.</summary>
        [PreserveSig] int GetStreamSelection();
        /// <summary>SetStreamSelection, unused.</summary>
        [PreserveSig] int SetStreamSelection();
        /// <summary>GetNativeMediaType, unused.</summary>
        [PreserveSig] int GetNativeMediaType();
        /// <summary>Return the stream's media type.</summary>
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        /// <summary>Set the stream's media type, converting where it can.</summary>
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        /// <summary>SetCurrentPosition, unused.</summary>
        [PreserveSig] int SetCurrentPosition();
        /// <summary>Read the next sample, blocking.</summary>
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
    }

    /// <summary>IMFSample, up to ConvertToContiguousBuffer.</summary>
    [GeneratedComInterface]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    internal partial interface IMFSample : IMFAttributes
    {
        /// <summary>GetSampleFlags, unused.</summary>
        [PreserveSig] int GetSampleFlags();
        /// <summary>SetSampleFlags, unused.</summary>
        [PreserveSig] int SetSampleFlags();
        /// <summary>GetSampleTime, unused.</summary>
        [PreserveSig] int GetSampleTime();
        /// <summary>SetSampleTime, unused.</summary>
        [PreserveSig] int SetSampleTime();
        /// <summary>GetSampleDuration, unused.</summary>
        [PreserveSig] int GetSampleDuration();
        /// <summary>SetSampleDuration, unused.</summary>
        [PreserveSig] int SetSampleDuration();
        /// <summary>GetBufferCount, unused.</summary>
        [PreserveSig] int GetBufferCount();
        /// <summary>GetBufferByIndex, unused.</summary>
        [PreserveSig] int GetBufferByIndex();
        /// <summary>Return the sample's data in one buffer.</summary>
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    /// <summary>IMFMediaBuffer, up to Unlock.</summary>
    [GeneratedComInterface]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    internal partial interface IMFMediaBuffer
    {
        /// <summary>Lock the buffer for reading.</summary>
        [PreserveSig] int Lock(out IntPtr data, out uint maxLength, out uint currentLength);
        /// <summary>Unlock the buffer.</summary>
        [PreserveSig] int Unlock();
    }
}
