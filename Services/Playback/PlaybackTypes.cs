namespace MusicTagRecover.Services.Playback;

/// <summary>播放失败时抛出（消息已本地化为用户可读文本）。</summary>
public sealed class PlaybackException : Exception
{
    public PlaybackException(string message) : base(message) { }
    public PlaybackException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 解码器产出的一个数据单元：音频块（float32 交错 PCM）或视频帧（BGRA32）。
/// 缓冲从引擎的缓冲池租出，消费方用完必须归还（见 <see cref="PlaybackBufferPool"/>）。
/// </summary>
public sealed class DecodedUnit
{
    public bool IsAudio;
    /// <summary>音频为 float32 交错 PCM；视频为 BGRA32 位图数据。</summary>
    public byte[] Buffer = Array.Empty<byte>();
    /// <summary>Buffer 中的有效字节数。</summary>
    public int Length;
    public int Width;
    public int Height;
    public int Stride;
    /// <summary>本单元首字节/首像素对应的播放时间。</summary>
    public TimeSpan Pts;

    public void Reset()
    {
        IsAudio = false;
        Buffer = Array.Empty<byte>();
        Length = 0;
        Width = Height = Stride = 0;
        Pts = TimeSpan.Zero;
    }
}

/// <summary>
/// 播放用字节缓冲池：解码侧租用、消费侧归还，避免每帧/每块重新分配大数组。
/// 线程安全；只按“不小于请求尺寸”复用，不做分桶，池内最多保留少量大缓冲。
/// </summary>
internal sealed class PlaybackBufferPool
{
    private readonly object _gate = new();
    private readonly List<byte[]> _free = new();
    private readonly int _maxRetained;

    public PlaybackBufferPool(int maxRetained = 12) => _maxRetained = maxRetained;

    public byte[] Rent(int minSize)
    {
        lock (_gate)
        {
            // 找最小够用的一块，避免大缓冲被小块请求长期占用
            int best = -1;
            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i].Length >= minSize && (best < 0 || _free[i].Length < _free[best].Length))
                    best = i;
            }
            if (best >= 0)
            {
                var buf = _free[best];
                _free.RemoveAt(best);
                return buf;
            }
        }
        return new byte[minSize];
    }

    public void Return(byte[]? buffer)
    {
        if (buffer == null || buffer.Length == 0) return;
        lock (_gate)
        {
            if (_free.Count < _maxRetained) _free.Add(buffer);
        }
    }
}
