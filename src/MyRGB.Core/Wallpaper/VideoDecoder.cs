using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyRGB.Core.Wallpaper;

/// <summary>
/// Reads a video file frame by frame with Windows' own Media Foundation, so MyRGB needs no codec of
/// its own: whatever Windows can play (MP4/H.264 always; HEVC, VP9 or AV1 with Microsoft's free
/// extensions), it can show. Frames come out as BGRA, top row first, scaled down by Windows to fit
/// within the size asked for. Not thread-safe: one thread reads.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoDecoder : IDisposable
{
    private const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;
    private const uint EndOfStream = 0x2;
    private static int _started;

    private IMFSourceReader? _reader;

    public int Width { get; }
    public int Height { get; }

    /// <summary>How long the video runs; zero when Windows doesn't say.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Open <paramref name="path"/>, with frames no bigger than <paramref name="maxWidth"/> by <paramref name="maxHeight"/>.</summary>
    /// <exception cref="VideoException">Windows can't open or decode the file.</exception>
    public VideoDecoder(string path, int maxWidth, int maxHeight)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Check(MFStartup(0x00020070, 0), "start Media Foundation");

        Check(MFCreateAttributes(out var attrs, 3), "set up the reader");
        try
        {
            // Let Windows convert to RGB and resize, using the graphics card's decoder where it can.
            attrs.SetUINT32(EnableAdvancedVideoProcessing, 1);
            attrs.SetUINT32(EnableHardwareTransforms, 1);
            int hr = MFCreateSourceReaderFromURL(path, attrs, out var reader);
            if (hr < 0) throw new VideoException("Windows couldn't open this file as a video.", hr);
            _reader = reader;
        }
        finally { Marshal.ReleaseComObject(attrs); }

        Check(_reader.SetStreamSelection(AllStreams, false), "pick the video stream");
        Check(_reader.SetStreamSelection(FirstVideoStream, true), "pick the video stream");

        // The video's own size, to keep its shape when scaling down.
        int hr2 = _reader.GetNativeMediaType(FirstVideoStream, 0, out var native);
        if (hr2 < 0) throw new VideoException("This file has no video in it.", hr2);
        int w, h;
        try
        {
            Check(native.GetUINT64(FrameSize, out ulong size), "read the video's size");
            (w, h) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
        }
        finally { Marshal.ReleaseComObject(native); }
        if (w <= 0 || h <= 0) throw new VideoException("The video reports no picture size.", 0);

        double scale = Math.Min(1, Math.Min(maxWidth / (double)w, maxHeight / (double)h));
        // Even sizes: some converters refuse odd ones.
        int outW = Math.Max(2, (int)(w * scale) & ~1), outH = Math.Max(2, (int)(h * scale) & ~1);

        if (!TrySetOutput(outW, outH))
        {
            // Some decoders won't resize; take the full size rather than nothing.
            (outW, outH) = (w & ~1, h & ~1);
            if (!TrySetOutput(outW, outH) && !TrySetOutput(0, 0))
                throw new VideoException("Windows can't decode this video's format. HEVC, VP9 and AV1 videos need Microsoft's video extensions from the Store.", 0);
        }

        // What was really agreed on.
        Check(_reader.GetCurrentMediaType(FirstVideoStream, out var current), "read the output format");
        try
        {
            Check(current.GetUINT64(FrameSize, out ulong agreed), "read the output size");
            (Width, Height) = ((int)(agreed >> 32), (int)(agreed & 0xFFFFFFFF));
        }
        finally { Marshal.ReleaseComObject(current); }

        var pv = new PropVariant();
        if (_reader.GetPresentationAttribute(MediaSource, PresentationDuration, ref pv) >= 0 && pv.Type == 21 /* VT_UI8 */)
            Duration = TimeSpan.FromTicks(pv.Value); // 100 ns units, as TimeSpan
    }

    private bool TrySetOutput(int width, int height)
    {
        Check(MFCreateMediaType(out var type), "describe the output");
        try
        {
            type.SetGUID(MajorType, VideoMajor);
            type.SetGUID(Subtype, Rgb32);
            if (width > 0) type.SetUINT64(FrameSize, (ulong)width << 32 | (uint)height);
            return _reader!.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type) >= 0;
        }
        finally { Marshal.ReleaseComObject(type); }
    }

    /// <summary>
    /// Decode the next frame into <paramref name="bgra"/> (at least Width × Height × 4 bytes). Returns
    /// its time in the video, or null at the end (call <see cref="Rewind"/> to loop).
    /// </summary>
    public TimeSpan? ReadFrame(byte[] bgra)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoDecoder));
        while (true)
        {
            Check(reader.ReadSample(FirstVideoStream, 0, out _, out uint flags, out long time, out var sample), "decode a frame");
            if ((flags & EndOfStream) != 0)
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
                return null;
            }
            if (sample is null) continue; // a gap in the stream; ask again
            try
            {
                Check(sample.ConvertToContiguousBuffer(out var buffer), "read a frame");
                try
                {
                    Check(buffer.Lock(out var data, out _, out int length), "read a frame");
                    try { Marshal.Copy(data, bgra, 0, Math.Min(length, Math.Min(bgra.Length, Width * Height * 4))); }
                    finally { buffer.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buffer); }
            }
            finally { Marshal.ReleaseComObject(sample); }
            return TimeSpan.FromTicks(time);
        }
    }

    /// <summary>Back to the first frame.</summary>
    public void Rewind()
    {
        var pv = new PropVariant { Type = 20 /* VT_I8 */, Value = 0 };
        Check(_reader!.SetCurrentPosition(Guid.Empty, ref pv), "go back to the start");
    }

    public void Dispose()
    {
        if (_reader is { } r) Marshal.ReleaseComObject(r);
        _reader = null;
    }

    private static void Check(int hr, string doing)
    {
        if (hr < 0) throw new VideoException($"Windows couldn't {doing} (0x{hr:X8}).", hr);
    }

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private const uint MediaSource = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort _r1, _r2, _r3;
        public long Value;
        private long _pad;
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

    // COM interfaces, declared in vtable order. Only the methods called have real signatures; the
    // rest hold their slot. IMFMediaType and IMFSample repeat IMFAttributes' 30 methods first.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
        void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
        void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
        void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32();
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection();
        [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(in Guid format, ref PropVariant position);
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        void Flush();
        void GetServiceForStream();
        [PreserveSig] int GetPresentationAttribute(uint stream, in Guid key, ref PropVariant value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
        void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
        void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64();
        void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime();
        void GetSampleDuration(); void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(); void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength(); void SetCurrentLength(); void GetMaxLength();
    }
}

/// <summary>A video Windows couldn't open or decode; the message says why in plain words.</summary>
public sealed class VideoException(string message, int hResult) : Exception(message)
{
    public int Code { get; } = hResult;
}
