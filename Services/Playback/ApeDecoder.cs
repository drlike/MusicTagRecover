using System.Runtime.InteropServices;

namespace MusicTagRecover.Services.Playback;

/// <summary>
/// APE 专用解码器：直接调用 Monkey's Audio 官方 SDK 的 MACDll64.dll（c_APEDecompress_* 系列导出）。
/// FFmpeg 解码 APE 在部分文件上会出现卡顿/爆音，官方 SDK 是格式作者的参考实现，输出 PCM 稳定可靠。
/// 输出统一转换为 float32 交错 PCM（多声道取前两声道压成立体声）。
/// </summary>
internal sealed unsafe class ApeDecoder : IPreviewDecoder
{
    // ---- MACDll64 导出函数签名（全部为 __stdcall，见 SDK 头文件 MACDll.h）----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr FnCreateW([MarshalAs(UnmanagedType.LPWStr)] string filename, out int errorCode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FnDestroy(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnGetData(IntPtr handle, IntPtr buffer, long blocks, out long blocksRetrieved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnSeek(IntPtr handle, long blockOffset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long FnGetInfo(IntPtr handle, int field, long param1, long param2);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnSetThreads(IntPtr handle, int threads);

    // GetInfo 字段编号（见 SDK 头文件 MACLib.h 的 APE_INFO_FIELDS）
    private const int InfoFormatFlags = 1002;
    private const int InfoSampleRate = 1003;
    private const int InfoBitsPerSample = 1004;
    private const int InfoBytesPerSample = 1005;
    private const int InfoChannels = 1006;
    private const int InfoBlockAlign = 1007;
    private const int InfoTotalBlocks = 1016;
    private const int InfoLengthMs = 1017;

    // APE 格式标志位（MACLib.h）
    private const long FlagBigEndian = 1L << 9;
    private const long FlagSigned8Bit = 1L << 11;
    private const long FlagFloatingPoint = 1L << 12;

    /// <summary>每次 GetData 解出的样本数（每声道），约 100~400ms 音频。</summary>
    private const int ChunkBlocks = 16384;

    private readonly FnDestroy _destroy;
    private readonly FnGetData _getData;
    private readonly FnSeek _seek;
    private readonly FnGetInfo _getInfo;

    private IntPtr _handle;
    private readonly int _rate;
    private readonly int _srcChannels;
    private readonly int _bits;
    private readonly int _blockAlign;
    private readonly long _totalBlocks;
    private readonly bool _floatingPoint;
    private readonly bool _bigEndian;
    private readonly bool _signed8Bit;
    private readonly IntPtr _pcmBuffer;
    private long _currentBlock;
    private bool _disposed;

    /// <summary>按扩展名判断是否为 Monkey's Audio 文件。</summary>
    public static bool IsApeFile(string path)
        => string.Equals(Path.GetExtension(path), ".ape", StringComparison.OrdinalIgnoreCase);

    /// <summary>DLL 搜索路径：输出目录下的 mac\ 子目录（由 csproj 拷贝规则保证存在）。</summary>
    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "mac", "MACDll64.dll");

    public static bool IsAvailable => File.Exists(DllPath);

    public ApeDecoder(string path)
    {
        if (!IsAvailable)
            throw new PlaybackException($"缺少 APE 解码组件（{DllPath}），请重新安装本软件。");

        IntPtr lib = NativeLibrary.Load(DllPath);
        try
        {
            var create = GetDelegate<FnCreateW>(lib, "c_APEDecompress_CreateW");
            _destroy = GetDelegate<FnDestroy>(lib, "c_APEDecompress_Destroy");
            _getData = GetDelegate<FnGetData>(lib, "c_APEDecompress_GetData");
            _seek = GetDelegate<FnSeek>(lib, "c_APEDecompress_Seek");
            _getInfo = GetDelegate<FnGetInfo>(lib, "c_APEDecompress_GetInfo");
            var setThreads = GetDelegate<FnSetThreads>(lib, "c_APEDecompress_SetNumberOfThreads");

            _handle = create(path, out int err);
            if (_handle == IntPtr.Zero || err != 0)
                throw new PlaybackException($"APE 文件无法解码（错误码 {err}）：文件已损坏或不是有效的 Monkey's Audio 格式。");

            // 解码线程数：适度并行即可，解码是 I/O + 轻 CPU 操作
            try { setThreads(_handle, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)); } catch { /* 旧版无此导出时忽略 */ }

            _rate = (int)_getInfo(_handle, InfoSampleRate, 0, 0);
            _bits = (int)_getInfo(_handle, InfoBitsPerSample, 0, 0);
            _srcChannels = (int)_getInfo(_handle, InfoChannels, 0, 0);
            _blockAlign = (int)_getInfo(_handle, InfoBlockAlign, 0, 0);
            _totalBlocks = _getInfo(_handle, InfoTotalBlocks, 0, 0);
            long flags = _getInfo(_handle, InfoFormatFlags, 0, 0);
            _floatingPoint = (flags & FlagFloatingPoint) != 0;
            _bigEndian = (flags & FlagBigEndian) != 0;
            _signed8Bit = (flags & FlagSigned8Bit) != 0;

            if (_rate <= 0 || _srcChannels <= 0 || _blockAlign <= 0)
                throw new PlaybackException("APE 文件头信息异常，文件可能已损坏。");

            long ms = _getInfo(_handle, InfoLengthMs, 0, 0);
            Duration = ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : TimeSpan.FromSeconds(_totalBlocks / (double)_rate);

            _pcmBuffer = Marshal.AllocHGlobal(ChunkBlocks * _blockAlign);
        }
        catch
        {
            if (_handle != IntPtr.Zero) { _destroy?.Invoke(_handle); _handle = IntPtr.Zero; }
            NativeLibrary.Free(lib);
            throw;
        }
        // 句柄存活期间 DLL 不能卸载：把 NativeLibrary 句柄留到 Dispose
        _libraryHandle = lib;
    }

    private readonly IntPtr _libraryHandle;

    private static T GetDelegate<T>(IntPtr lib, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    public TimeSpan Duration { get; }
    public bool HasAudio => true;
    public bool HasVideo => false;
    public int SampleRate => _rate;
    public int Channels => Math.Min(_srcChannels, 2);
    public int VideoWidth => 0;
    public int VideoHeight => 0;
    public string AudioCodecName => $"Monkey's Audio (APE {_bits}-bit)";
    public string? VideoCodecName => null;

    public bool DecodeNext(DecodedUnit unit, Func<int, byte[]> rent)
    {
        if (_handle == IntPtr.Zero || _currentBlock >= _totalBlocks) return false;

        int ret = _getData(_handle, _pcmBuffer, ChunkBlocks, out long got);
        if (ret != 0 || got <= 0) return false;   // 出错或 EOF

        int blocks = (int)got;
        int outCh = Channels;
        int outBytes = blocks * outCh * sizeof(float);
        unit.Buffer = rent(outBytes);
        unit.IsAudio = true;
        unit.Length = outBytes;
        unit.Pts = TimeSpan.FromSeconds(_currentBlock / (double)_rate);
        _currentBlock += blocks;

        fixed (byte* dst = unit.Buffer)
            ConvertToFloat((byte*)_pcmBuffer, (float*)dst, blocks, outCh);
        return true;
    }

    /// <summary>把 MAC 输出的原生 PCM（8/16/24/32 位整型或 32 位浮点）转成 float32。</summary>
    private void ConvertToFloat(byte* src, float* dst, int blocks, int outCh)
    {
        int frames = blocks;
        int srcCh = _srcChannels;
        int bytesPerSample = _blockAlign / Math.Max(1, srcCh);

        if (!_floatingPoint && _bits == 16 && bytesPerSample == 2 && !_bigEndian)
        {
            var s16 = (short*)src;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                    *dst++ = s16[i * srcCh + c] * (1f / 32768f);
        }
        else if (!_floatingPoint && _bits == 8 && bytesPerSample == 1)
        {
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                {
                    byte b = src[i * srcCh + c];
                    // 无符号 8 位（WAV 惯例）以 128 为零点；带符号 8 位（AIFF 惯例）直接映射
                    *dst++ = _signed8Bit ? (sbyte)b * (1f / 128f) : (b - 128) * (1f / 128f);
                }
        }
        else if (!_floatingPoint && _bits == 24 && bytesPerSample == 3 && !_bigEndian)
        {
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                {
                    byte* p = src + (i * srcCh + c) * 3;
                    int v = p[0] | (p[1] << 8) | (p[2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);   // 24 位符号扩展
                    *dst++ = v * (1f / 8388608f);
                }
        }
        else if (!_floatingPoint && _bits == 32 && bytesPerSample == 4 && !_bigEndian)
        {
            var s32 = (int*)src;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                    *dst++ = s32[i * srcCh + c] * (1f / 2147483648f);
        }
        else if (_floatingPoint && bytesPerSample == 4 && !_bigEndian)
        {
            var f32 = (float*)src;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                    *dst++ = f32[i * srcCh + c];
        }
        else
        {
            // 大端（AIFF 源）或其它罕见组合：按字节组装整数后转换
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < outCh; c++)
                {
                    byte* p = src + (i * srcCh + c) * bytesPerSample;
                    long v = 0;
                    if (_bigEndian)
                        for (int b = 0; b < bytesPerSample; b++) v = (v << 8) | p[b];
                    else
                        for (int b = bytesPerSample - 1; b >= 0; b--) v = (v << 8) | p[b];
                    int bits = bytesPerSample * 8;
                    long signBit = 1L << (bits - 1);
                    if ((v & signBit) != 0) v -= 1L << bits;                    // 符号扩展
                    *dst++ = (float)(v / (double)signBit);
                }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_handle == IntPtr.Zero) return;
        long block = Math.Clamp((long)(position.TotalSeconds * _rate), 0, Math.Max(0, _totalBlocks - 1));
        if (_seek(_handle, block) == 0)
            _currentBlock = block;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { _destroy(_handle); } catch { /* 退出时忽略 */ }
            _handle = IntPtr.Zero;
        }
        if (_pcmBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_pcmBuffer);
        if (_libraryHandle != IntPtr.Zero) NativeLibrary.Free(_libraryHandle);
    }
}
