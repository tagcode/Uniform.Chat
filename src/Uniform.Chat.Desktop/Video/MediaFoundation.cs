namespace Uniform.Chat.Desktop.Video;

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

/// <summary>The calls of <see href="https://learn.microsoft.com/windows/win32/medfound/media-foundation-programming-guide">Media Foundation</see> that the Windows camera, <see cref="Cameras.MediaFoundationCamera"/>, and video messages, <see cref="MediaFoundationVideoSource"/>, make: P/Invoke into the system's libraries, its COM interfaces source-generated, and whether it is installed.</summary>
/// <remarks>The COM interfaces declare their methods in the Windows SDK headers' vtable order up to the last one used; slots before it that are never called keep placeholders. Media Foundation's objects are free-threaded: each thread that calls them joins the multithreaded apartment first, by <see cref="EnterMta"/>.</remarks>
internal static partial class MediaFoundation
{
    /// <summary>The version of Media Foundation asked for, MF_VERSION of Windows 7 and later.</summary>
    private const uint Version = 0x00020070;

    /// <summary>The source reader's first video stream, first audio stream, all its streams, and the media source itself.</summary>
    public const uint FirstVideoStream = 0xFFFFFFFC, FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;

    /// <summary>Flags of a read sample: an error, the end of the stream, and a new media type of the stream.</summary>
    public const uint ReadError = 0x1, EndOfStream = 0x2, MediaTypeChanged = 0x20;

    /// <summary>The variant types of a 64-bit signed and unsigned integer.</summary>
    public const ushort VariantInt64 = 20, VariantUInt64 = 21;

    /// <summary>Read access of a 2-D buffer's lock.</summary>
    public const int LockRead = 1;

    /// <summary>Attributes of a media type: its major type, subtype and frame size.</summary>
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>Attributes of a video media type: the bytes from one row to the next, the area to show, and the clockwise turn that shows the pictures upright, as an MP4's matrix tells.</summary>
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4"), VideoRotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");

    /// <summary>Attributes of an audio media type: its channels, samples a second, bits a sample, bytes a frame and bytes a second.</summary>
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), AudioSampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d"), AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    /// <summary>Major types and subtypes: video, NV12 pictures, audio, and 16-bit PCM.</summary>
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71"), Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>Source reader attributes: convert YUV to RGB and deinterlace; and also convert between YUV formats and sizes, e.g. 10-bit pictures to NV12.</summary>
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    /// <summary>A presentation's length, in 100 ns.</summary>
    public static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    /// <summary>The wrappers of COM pointers returned in arrays.</summary>
    public static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Lazy<bool> available = new(Initialize);

    [ThreadStatic]
    private static bool inMta;

    /// <summary>Whether Media Foundation is installed and started; false off Windows, and on Windows N editions without the Media Feature Pack.</summary>
    public static bool IsAvailable => available.Value;

    /// <summary>Join this thread to the multithreaded apartment, once per thread; a thread already in a single-threaded one stays there, which free-threaded objects allow.</summary>
    public static void EnterMta()
    {
        // Once per thread
        if (inMta) return;
        CoInitializeEx(0, 0);
        // Done
        inMta = true;
    }

    /// <summary>Release a COM object now, rather than when collected.</summary>
    public static void Release(object? instance)
    {
        // Wrappers of the source-generated interfaces
        if (instance is ComObject com) com.FinalRelease();
    }

    /// <summary>Throw for a failed HRESULT.</summary>
    public static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    /// <summary>Start Media Foundation once; return whether it started.</summary>
    private static bool Initialize()
    {
        // Windows with its libraries
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return MFStartup(Version, 0) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // On no Media Foundation, as on Windows N without its media pack
            return false;
        }
    }

    /// <summary>Join the thread to a COM apartment.</summary>
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    /// <summary>Media Foundation's start.</summary>
    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    /// <summary>A new attribute store.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    /// <summary>A new media type.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out IMFMediaType type);

    /// <summary>The devices of the type the attributes name; an array of activation objects to free.</summary>
    [LibraryImport("mf.dll")]
    public static partial int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activates, out uint count);

    /// <summary>A synchronous reader of a media source.</summary>
    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSourceReaderFromMediaSource(IntPtr source, IMFAttributes attributes, out IMFSourceReader reader);

    /// <summary>A synchronous reader of a file.</summary>
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);

    /// <summary>A PROPVARIANT holding a 64-bit integer, as a source reader's position and presentation attributes are; as large as the union on 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        /// <summary>The variant type.</summary>
        public ushort Type;
        private readonly ushort reserved1, reserved2, reserved3;
        /// <summary>The integer.</summary>
        public long Value;
        private readonly nint rest;
    }

    /// <summary>An MFVideoArea: the offsets of an area, each a fraction and a whole value, and its size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VideoArea
    {
        /// <summary>The fraction of the horizontal offset.</summary>
        public ushort XFraction;
        /// <summary>The horizontal offset in pixels.</summary>
        public short X;
        /// <summary>The fraction of the vertical offset.</summary>
        public ushort YFraction;
        /// <summary>The vertical offset in pixels.</summary>
        public short Y;
        /// <summary>The width in pixels.</summary>
        public int Width;
        /// <summary>The height in pixels.</summary>
        public int Height;
    }

    /// <summary>IMFAttributes, whole, as other interfaces extend it.</summary>
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
        /// <summary>Return a 32-bit value.</summary>
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        /// <summary>Return a 64-bit value.</summary>
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        /// <summary>GetDouble, unused.</summary>
        [PreserveSig] int GetDouble();
        /// <summary>Return a GUID value.</summary>
        [PreserveSig] int GetGUID(in Guid key, out Guid value);
        /// <summary>GetStringLength, unused.</summary>
        [PreserveSig] int GetStringLength();
        /// <summary>GetString, unused.</summary>
        [PreserveSig] int GetString();
        /// <summary>Return a string value in memory the caller frees with CoTaskMemFree.</summary>
        [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
        /// <summary>GetBlobSize, unused.</summary>
        [PreserveSig] int GetBlobSize();
        /// <summary>Return a blob value that is an area.</summary>
        [PreserveSig] int GetBlob(in Guid key, out VideoArea area, uint size, out uint written);
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

    /// <summary>IMFSourceReader, up to GetPresentationAttribute.</summary>
    [GeneratedComInterface]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    internal partial interface IMFSourceReader
    {
        /// <summary>GetStreamSelection, unused.</summary>
        [PreserveSig] int GetStreamSelection();
        /// <summary>Select or deselect a stream, or all of them.</summary>
        [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        /// <summary>Return one of the stream's types as the file has it.</summary>
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        /// <summary>Return the stream's media type.</summary>
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        /// <summary>Set the stream's media type, decoding and converting where it can.</summary>
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        /// <summary>Move to a position, in 100 ns for a null format: to the keyframe before it.</summary>
        [PreserveSig] int SetCurrentPosition(in Guid format, in PropVariant position);
        /// <summary>Read the next sample, blocking.</summary>
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        /// <summary>Flush, unused.</summary>
        [PreserveSig] int Flush();
        /// <summary>GetServiceForStream, unused.</summary>
        [PreserveSig] int GetServiceForStream();
        /// <summary>Return an attribute of a stream or of the presentation, e.g. its length.</summary>
        [PreserveSig] int GetPresentationAttribute(uint stream, in Guid key, out PropVariant value);
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
        /// <summary>Lock the buffer for reading, its rows the default stride apart.</summary>
        [PreserveSig] int Lock(out IntPtr data, out uint maxLength, out uint currentLength);
        /// <summary>Unlock the buffer.</summary>
        [PreserveSig] int Unlock();
    }

    /// <summary>IMF2DBuffer, up to Unlock2D; the base of <see cref="IMF2DBuffer2"/>.</summary>
    [GeneratedComInterface]
    [Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")]
    internal partial interface IMF2DBuffer
    {
        /// <summary>Lock2D, unused.</summary>
        [PreserveSig] int Lock2D();
        /// <summary>Unlock the buffer.</summary>
        [PreserveSig] int Unlock2D();
        /// <summary>GetScanline0AndPitch, unused.</summary>
        [PreserveSig] int GetScanline0AndPitch();
        /// <summary>IsContiguousFormat, unused.</summary>
        [PreserveSig] int IsContiguousFormat();
        /// <summary>GetContiguousLength, unused.</summary>
        [PreserveSig] int GetContiguousLength();
        /// <summary>ContiguousCopyTo, unused.</summary>
        [PreserveSig] int ContiguousCopyTo();
        /// <summary>ContiguousCopyFrom, unused.</summary>
        [PreserveSig] int ContiguousCopyFrom();
    }

    /// <summary>IMF2DBuffer2, up to Lock2DSize: a video buffer in its own layout, without a copy.</summary>
    [GeneratedComInterface]
    [Guid("33ae5ea6-4316-436f-8ddd-d73d22f829ec")]
    internal partial interface IMF2DBuffer2 : IMF2DBuffer
    {
        /// <summary>Lock the buffer: its first row, the bytes from a row to the next, and where the memory starts and how long it is.</summary>
        [PreserveSig] int Lock2DSize(int flags, out IntPtr scanline0, out int pitch, out IntPtr bufferStart, out uint bufferLength);
    }
}
