namespace Uniform.Chat.Desktop.Cameras;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Uniform.Matrix.Video;

/// <summary>A Linux webcam through <see href="https://www.kernel.org/doc/html/latest/userspace-api/media/v4l/v4l2.html">Video4Linux2</see>: YUYV pictures of 640x480 from memory-mapped buffers, converted to I420 and encoded by <see cref="EncodingCamera"/>; 320x240 when the peer asks for less.</summary>
/// <remarks>Only <c>open</c>, <c>ioctl</c>, <c>mmap</c>, <c>munmap</c> and <c>close</c> of libc are called; the structures are byte arrays at the offsets of 64-bit Linux. Inter frames between keyframes keep the bitrate down; a keyframe request makes the next frame one.</remarks>
public sealed class V4l2Camera : EncodingCamera
{
    private const uint QueryCapability = 0x80685600, SetFormat = 0xC0D05605, RequestBuffers = 0xC0145608, QueryBuffer = 0xC0585609, QueueBuffer = 0xC058560F, DequeueBuffer = 0xC0585611, StreamOn = 0x40045612, StreamOff = 0x40045613;
    private const uint VideoCapture = 1, MemoryMap = 1, Yuyv = 0x56595559, CaptureCapability = 1, DeviceCapabilities = 0x80000000, FieldNone = 1;
    private const int ReadWrite = 2, ProtectReadWrite = 3, MapShared = 1, BufferCount = 4;
    private readonly List<(IntPtr Address, int Length)> buffers = [];
    private CancellationTokenSource? stopping;
    private Thread? capturing;
    private int device = -1;
    private int index;
    private int width;
    private int height;
    private int captureCount;

    /// <summary>Return whether this machine has a video device that captures, e.g. <c>/dev/video0</c>.</summary>
    public static bool Available() => OperatingSystem.IsLinux() && CaptureDevices().Any();

    /// <summary>Whether another device captures, as counted at start; a webcam's metadata node does not.</summary>
    public override bool CanSwitch => captureCount > 1;

    /// <summary>Open the first capture device and start capturing on a thread of its own.</summary>
    public override void Start()
    {
        // The first device that captures, and how many do
        int[] capturing = [.. CaptureDevices()];
        captureCount = capturing.Length;
        foreach (int candidate in capturing)
        {
            if (!Open(candidate)) continue;
            index = candidate;
            break;
        }
        if (device < 0) throw new IOException("No video device captures YUYV.");
        // Done
        Run();
    }

    /// <summary>Open the next video device that captures, after the current one.</summary>
    public override void SwitchCamera()
    {
        // The capturing devices after this one, then from the start
        int[] all = [.. CaptureDevices()];
        if (all.Length < 2) return;
        Stop();
        foreach (int candidate in all.Where(d => d > index).Concat(all.Where(d => d <= index)))
        {
            if (!Open(candidate)) continue;
            index = candidate;
            break;
        }
        // Done
        if (device >= 0) Run();
    }

    /// <summary>Stop capturing and close the device.</summary>
    public override ValueTask DisposeAsync()
    {
        // The thread, then the device
        Stop();
        // Done
        return ValueTask.CompletedTask;
    }

    /// <summary>Return the numbers of <c>/dev/videoN</c> devices, in order.</summary>
    private static IEnumerable<int> Devices() =>
        Directory.Exists("/dev") ? Directory.GetFiles("/dev", "video*").Select(p => int.TryParse(Path.GetFileName(p)[5..], out int n) ? n : -1).Where(n => n >= 0).Order() : [];

    /// <summary>Return the numbers of the devices that capture video, asked for their capabilities; a webcam's metadata node is left out.</summary>
    private static IEnumerable<int> CaptureDevices() => Devices().Where(Captures);

    /// <summary>Return whether device <paramref name="number"/> captures video; false when it cannot be opened, e.g. while in use.</summary>
    private static bool Captures(int number)
    {
        // Its capabilities, then closed
        int fd = open($"/dev/video{number}", ReadWrite);
        if (fd < 0) return false;
        byte[] capability = new byte[104];
        bool captures = ioctl(fd, QueryCapability, capability) >= 0 && (NodeCapabilities(capability) & CaptureCapability) != 0;
        close(fd);
        // Return answer
        return captures;
    }

    /// <summary>Return the capabilities of the device node: its own when the kernel tells them, else the device's.</summary>
    private static uint NodeCapabilities(byte[] capability)
    {
        // Device capabilities are valid with their flag
        uint all = BinaryPrimitives.ReadUInt32LittleEndian(capability.AsSpan(84));
        // Return node's
        return (all & DeviceCapabilities) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(capability.AsSpan(88)) : all;
    }

    /// <summary>Open device <paramref name="number"/> for YUYV capture at 640x480 into memory-mapped buffers, streaming; return whether it works.</summary>
    private bool Open(int number)
    {
        // A capture device
        int fd = open($"/dev/video{number}", ReadWrite);
        if (fd < 0) return false;
        byte[] capability = new byte[104];
        if (ioctl(fd, QueryCapability, capability) < 0) return Fail(fd);
        if ((NodeCapabilities(capability) & CaptureCapability) == 0) return Fail(fd);

        // YUYV at 640x480, or the size the device takes instead
        byte[] format = new byte[208];
        BinaryPrimitives.WriteUInt32LittleEndian(format, VideoCapture);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), 640);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(12), 480);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(16), Yuyv);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(20), FieldNone);
        if (ioctl(fd, SetFormat, format) < 0 || BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(16)) != Yuyv) return Fail(fd);
        width = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
        height = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(12));

        // Buffers, mapped and queued
        byte[] request = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(request, BufferCount);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), VideoCapture);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), MemoryMap);
        if (ioctl(fd, RequestBuffers, request) < 0) return Fail(fd);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(request);
        for (int i = 0; i < count; i++)
        {
            byte[] buffer = Buffer(i);
            if (ioctl(fd, QueryBuffer, buffer) < 0) return Fail(fd);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(72));
            IntPtr address = mmap(IntPtr.Zero, (nuint)length, ProtectReadWrite, MapShared, fd, (nint)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64)));
            if (address == new IntPtr(-1)) return Fail(fd);
            buffers.Add((address, length));
            if (ioctl(fd, QueueBuffer, buffer) < 0) return Fail(fd);
        }

        // Streaming
        byte[] type = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(type, VideoCapture);
        if (ioctl(fd, StreamOn, type) < 0) return Fail(fd);
        device = fd;
        // Return opened
        return true;
    }

    /// <summary>Unmap the buffers, close <paramref name="fd"/>, and return false.</summary>
    private bool Fail(int fd)
    {
        // Mappings, then the device
        foreach ((IntPtr address, int length) in buffers) munmap(address, (nuint)length);
        buffers.Clear();
        close(fd);
        // On failure
        return false;
    }

    /// <summary>Return a buffer structure for buffer <paramref name="number"/>, memory-mapped capture.</summary>
    private static byte[] Buffer(int number)
    {
        // Index, type and memory
        byte[] buffer = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)number);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), VideoCapture);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), MemoryMap);
        // Return buffer
        return buffer;
    }

    /// <summary>Start the capture thread.</summary>
    private void Run()
    {
        // Until stopped
        stopping = new CancellationTokenSource();
        CancellationToken token = stopping.Token;
        capturing = new Thread(() => Capture(token)) { IsBackground = true, Name = "camera" };
        capturing.Start();
    }

    /// <summary>Take frames until stopped: each buffer copied out and queued again, then converted and encoded at the frame rate.</summary>
    private void Capture(CancellationToken token)
    {
        // Dequeue blocks until a picture is ready
        byte[] yuyv = [];
        while (!token.IsCancellationRequested)
        {
            byte[] buffer = Buffer(0);
            if (ioctl(device, DequeueBuffer, buffer) < 0) break;
            int number = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            int used = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
            if (yuyv.Length != used) yuyv = new byte[used];
            Marshal.Copy(buffers[number].Address, yuyv, 0, Math.Min(used, buffers[number].Length));
            ioctl(device, QueueBuffer, buffer);

            // Paced, converted and encoded by the base
            byte[] captured = yuyv;
            Deliver(() => ToI420(captured, width, height, Low));
        }
    }

    /// <summary>Return YUYV, two pixels in four bytes, as I420; with <paramref name="half"/>, every other pixel and row, at half the size. Chroma is taken from every other row.</summary>
    internal static VideoFrame ToI420(byte[] yuyv, int width, int height, bool half)
    {
        // Sizes of the result
        int step = half ? 2 : 1;
        int w = width / step, h = height / step, cw = (w + 1) / 2, ch = (h + 1) / 2;
        byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
        int rowBytes = width * 2;

        // Luma of every kept pixel, chroma of every other kept row
        for (int row = 0; row < h; row++)
        {
            int source = row * step * rowBytes;
            for (int column = 0; column < w; column++)
            {
                int x = column * step;
                y[row * w + column] = yuyv[source + x * 2];
                if ((row & 1) != 0 || (column & 1) != 0) continue;
                int pair = source + (x & ~1) * 2;
                u[row / 2 * cw + column / 2] = yuyv[pair + 1];
                v[row / 2 * cw + column / 2] = yuyv[pair + 3];
            }
        }
        // Return picture
        return new VideoFrame(w, h, y, u, v);
    }

    /// <summary>Stop the thread, and stop streaming and close the device.</summary>
    private void Stop()
    {
        // Streaming off wakes the thread's dequeue
        stopping?.Cancel();
        if (device >= 0)
        {
            byte[] type = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(type, VideoCapture);
            ioctl(device, StreamOff, type);
        }
        capturing?.Join(TimeSpan.FromSeconds(2));
        capturing = null;
        if (device >= 0) Fail(device);
        // Done
        device = -1;
    }

    /// <summary>libc's open.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    /// <summary>libc's ioctl with a structure of bytes, pinned for the call.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, byte[] argument);

    /// <summary>libc's mmap.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fd, nint offset);

    /// <summary>libc's munmap.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr address, nuint length);

    /// <summary>libc's close.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
