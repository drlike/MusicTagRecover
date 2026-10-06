namespace MusicTagRecover.Services.Playback;

/// <summary>
/// 预览解码器抽象：把一种媒体格式解成统一的输出形态 ——
/// 音频统一为 float32 交错 PCM（源采样率，声道数见 <see cref="Channels"/>），
/// 视频统一为 BGRA32 帧。
/// 线程模型：DecodeNext / Seek 全部只在播放引擎的解码线程上调用（引擎保证串行），
/// 实现类内部无需再加锁。
/// </summary>
internal interface IPreviewDecoder : IDisposable
{
    /// <summary>媒体总时长（未知时为 Zero）。</summary>
    TimeSpan Duration { get; }

    bool HasAudio { get; }
    bool HasVideo { get; }

    /// <summary>输出 PCM 采样率（Hz）。</summary>
    int SampleRate { get; }

    /// <summary>输出 PCM 声道数（多声道会在此层被压成立体声）。</summary>
    int Channels { get; }

    int VideoWidth { get; }
    int VideoHeight { get; }

    /// <summary>音频编码名（用于界面展示）。</summary>
    string AudioCodecName { get; }

    /// <summary>视频编码名（无视频流时为 null）。</summary>
    string? VideoCodecName { get; }

    /// <summary>
    /// 解出下一单位数据写入 unit（unit.Buffer 已由调用方从缓冲池租好、尺寸足够）。
    /// 返回 false 表示流结束（含冲刷完解码器尾部帧之后）。
    /// </summary>
    /// <param name="unit">待填充的单元；IsAudio 由实现设置。</param>
    /// <param name="rent">视频帧等尺寸不定的缓冲租用委托（实现只在需要更大缓冲时调用）。</param>
    bool DecodeNext(DecodedUnit unit, Func<int, byte[]> rent);

    /// <summary>跳转到指定播放位置，并丢弃内部所有已解码未输出的数据。</summary>
    void Seek(TimeSpan position);
}
