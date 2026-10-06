using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace MusicTagRecover.Services.Playback;

/// <summary>
/// 通用媒体解码器：基于 FFmpeg.AutoGen 9 直接 P/Invoke FFmpeg 共享库，
/// 覆盖除 APE 外的全部主流音视频格式（FLAC/MP3/WAV/OGG/AAC/M4A/MP4/MKV/AVI/WMV/MOV/TS 等）。
/// 音频统一重采样为 float32 交错 PCM（保持源采样率，多声道压成立体声），
/// 视频统一转换为 BGRA32 帧。
/// 容错策略：解复用/解码错误一律跳过而非失败，损坏文件尽可能播放到无法继续的位置。
/// </summary>
internal sealed unsafe class FfmpegDecoder : IPreviewDecoder
{
    /// <summary>连续解码错误上限，超过即认为该流已不可用（按排空处理）。</summary>
    private const int MaxConsecutiveErrors = 32;

    /// <summary>DecodeNext 单次调用的循环保险丝，防止异常文件造成死循环。</summary>
    private const int MaxLoopIterations = 4096;

    private AVFormatContext* _fmt;
    private AVCodecContext* _audioCtx;
    private AVCodecContext* _videoCtx;
    private int _audioStreamIndex = -1;
    private int _videoStreamIndex = -1;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private SwsContext* _sws;
    private int _swsW, _swsH;
    private AVPixelFormat _swsFmt = AVPixelFormat.AV_PIX_FMT_NONE;

    // sws_scale 参数缓冲（避免每帧分配；仅解码线程访问）
    private readonly byte*[] _srcData = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];

    // 发送/接收状态机（见 DecodeNext）
    private bool _packetPending;            // 上次 send_packet 返回 EAGAIN，包仍归本类持有
    private AVCodecContext* _pendingTarget; // 未消费包的目标解码器
    private bool _demuxEof;                 // 解复用层已到文件尾
    private bool _flushSent;                // 是否已向解码器发送冲刷包
    private bool _audioDrained;             // 音频解码器已排空
    private bool _videoDrained;             // 视频解码器已排空
    private int _consecutiveErrors;

    private readonly int _sampleRate;
    private readonly int _outChannels;
    private readonly string? _audioCodecName;
    private readonly string? _videoCodecName;
    private TimeSpan _nextAudioPts;
    private TimeSpan _nextVideoPts;
    private bool _disposed;

    public FfmpegDecoder(string path)
    {
        // FFmpeg.AutoGen 9 的 avformat_open_input 仅提供 string 重载（内部按 UTF-8 封送，
        // FFmpeg 在 Windows 上再转宽字符打开，中文路径安全）
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (ret < 0 || fmt == null)
            throw new PlaybackException(
                $"无法打开媒体文件（{DescribeError(ret)}）：文件已损坏、被占用或格式不受支持。");
        _fmt = fmt;

        try
        {
            ret = ffmpeg.avformat_find_stream_info(fmt, null);
            if (ret < 0)
                throw new PlaybackException("无法读取媒体流信息：文件可能已损坏或不完整。");

            if (fmt->duration > 0)
                Duration = TimeSpan.FromSeconds(fmt->duration / (double)ffmpeg.AV_TIME_BASE);

            // ---- 音频流：让 FFmpeg 选最优流 ----
            _audioStreamIndex = ffmpeg.av_find_best_stream(
                fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_audioStreamIndex >= 0)
                _audioCtx = OpenDecoder(fmt->streams[_audioStreamIndex], out _audioCodecName);

            // ---- 视频流：手动遍历，跳过封面图等附件流 ----
            for (uint i = 0; i < fmt->nb_streams; i++)
            {
                AVStream* st = fmt->streams[i];
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO) continue;
                if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) continue;
                AVCodecContext* ctx = OpenDecoder(st, out string? name);
                if (ctx == null) continue;   // 无解码器的流跳过（如部分缩略图轨）
                _videoStreamIndex = (int)i;
                _videoCtx = ctx;
                _videoCodecName = name;
                VideoWidth = ctx->width;
                VideoHeight = ctx->height;
                break;
            }

            if (_audioCtx == null && _videoCtx == null)
                throw new PlaybackException("未找到可解码的音频或视频流：编码不受支持或文件已损坏。");

            if (_audioCtx != null)
            {
                _sampleRate = _audioCtx->sample_rate > 0 ? _audioCtx->sample_rate : 44100;
                // 多声道压成立体声输出（布局缺失时按立体声处理）
                int ch = _audioCtx->ch_layout.nb_channels;
                _outChannels = ch >= 2 ? 2 : 1;
            }

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null)
                throw new PlaybackException("FFmpeg 内存分配失败。");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>为指定流分配并打开解码器；无解码器或打开失败时返回 null（调用方按无此流处理）。</summary>
    private static AVCodecContext* OpenDecoder(AVStream* stream, out string? codecName)
    {
        codecName = null;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) return null;

        // avcodec_get_name 在 FFmpeg.AutoGen 9 中返回 string
        codecName = PtrToUtf8(codec->long_name)
                    ?? PtrToUtf8(codec->name)
                    ?? ffmpeg.avcodec_get_name(codec->id);

        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (ctx == null) return null;
        if (ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        // 帧级多线程解码（0 = 按 CPU 核数自动），降低高码率视频的解码耗时
        ctx->thread_count = 0;
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        return ctx;
    }

    public TimeSpan Duration { get; private set; }
    public bool HasAudio => _audioCtx != null;
    public bool HasVideo => _videoCtx != null;
    public int SampleRate => _sampleRate;
    public int Channels => _outChannels;
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public string AudioCodecName => _audioCodecName ?? "unknown";
    public string? VideoCodecName => _videoCodecName;

    public bool DecodeNext(DecodedUnit unit, Func<int, byte[]> rent)
    {
        if (_disposed || _fmt == null) return false;

        int guard = 0;
        while (guard++ < MaxLoopIterations)
        {
            // 1) 优先取已解码帧：先音频后视频，保证音频供给连续（防断音）
            if (_audioCtx != null && !_audioDrained)
            {
                if (Receive(_audioCtx, ref _audioDrained) == 0)
                {
                    FillAudio(unit, rent);
                    return true;
                }
            }
            if (_videoCtx != null && !_videoDrained)
            {
                if (Receive(_videoCtx, ref _videoDrained) == 0)
                {
                    FillVideo(unit, rent);
                    return true;
                }
            }

            if (IsDrained()) return false;

            // 2) 重发上次 EAGAIN 未消费的包
            if (_packetPending)
            {
                int r = ffmpeg.avcodec_send_packet(_pendingTarget, _packet);
                if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    ffmpeg.av_packet_unref(_packet);
                    _packetPending = false;   // 成功或损坏包：均已处理完毕
                    if (r < 0) OnDecodeError();
                }
                continue;   // EAGAIN：回到顶部先 receive 腾出空间
            }

            // 3) 解复用 EOF：向解码器送冲刷包，取尾部残留帧
            if (_demuxEof)
            {
                if (!_flushSent)
                {
                    _flushSent = true;
                    if (_audioCtx != null && !_audioDrained)
                        ffmpeg.avcodec_send_packet(_audioCtx, null);
                    if (_videoCtx != null && !_videoDrained)
                        ffmpeg.avcodec_send_packet(_videoCtx, null);
                }
                else
                {
                    // 冲刷后仍取不到帧（receive 持续 EAGAIN）：按全部排空处理
                    _audioDrained = _videoDrained = true;
                }
                continue;
            }

            // 4) 读下一个包；读错误一律按 EOF 处理，让损坏文件播到能播的位置
            if (ffmpeg.av_read_frame(_fmt, _packet) < 0)
            {
                _demuxEof = true;
                continue;
            }

            AVCodecContext* target = null;
            if (_packet->stream_index == _audioStreamIndex) target = _audioCtx;
            else if (_packet->stream_index == _videoStreamIndex) target = _videoCtx;
            if (target == null)
            {
                ffmpeg.av_packet_unref(_packet);   // 不关心其它流（字幕/数据轨等）
                continue;
            }

            int sendRet = ffmpeg.avcodec_send_packet(target, _packet);
            if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                _pendingTarget = target;
                _packetPending = true;   // 包所有权仍在本类，下轮先 receive 再重发
            }
            else
            {
                ffmpeg.av_packet_unref(_packet);
                if (sendRet < 0) OnDecodeError();   // 损坏包：丢弃继续
            }
        }

        // 保险丝熔断：按流结束处理，防止异常文件死循环
        _audioDrained = _videoDrained = true;
        return false;
    }

    /// <summary>从解码器取一帧；返回 0 表示成功（帧在 _frame 中）。</summary>
    private int Receive(AVCodecContext* ctx, ref bool drained)
    {
        int ret = ffmpeg.avcodec_receive_frame(ctx, _frame);
        if (ret == 0)
        {
            _consecutiveErrors = 0;
            return 0;
        }
        if (ret == ffmpeg.AVERROR_EOF)
        {
            drained = true;
            return ret;
        }
        if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            OnDecodeError();
            if (_consecutiveErrors >= MaxConsecutiveErrors) drained = true;
        }
        return ret;
    }

    private bool IsDrained()
        => (_audioCtx == null || _audioDrained) && (_videoCtx == null || _videoDrained);

    private void OnDecodeError() => _consecutiveErrors++;

    /// <summary>把 _frame 中的音频帧重采样为 float32 交错 PCM 写入 unit。</summary>
    private void FillAudio(DecodedUnit unit, Func<int, byte[]> rent)
    {
        EnsureResampler();

        // 输出上限 = 重采样器内部延迟残留 + 本帧样本数（输入输出同采样率，仅格式/布局转换）
        long pending = ffmpeg.swr_get_delay(_swr, _frame->sample_rate) + _frame->nb_samples;
        int outSamples = (int)ffmpeg.av_rescale_rnd(pending,
            _frame->sample_rate, _frame->sample_rate, AVRounding.AV_ROUND_UP);
        if (outSamples < _frame->nb_samples) outSamples = _frame->nb_samples;

        byte[] buf = rent(outSamples * _outChannels * sizeof(float));

        // extended_data 是 byte**（平面格式每声道一平面，交错格式只用 [0]）
        byte** inData = stackalloc byte*[8];
        for (int i = 0; i < 8; i++) inData[i] = _frame->extended_data[i];
        byte** outData = stackalloc byte*[1];

        int converted;
        fixed (byte* pBuf = buf)
        {
            outData[0] = pBuf;
            converted = ffmpeg.swr_convert(_swr, outData, outSamples, inData, _frame->nb_samples);
        }
        if (converted < 0) converted = 0;

        // 时间戳：优先 best_effort_timestamp，缺失时按已输出样本数推算
        AVStream* stream = _fmt->streams[_audioStreamIndex];
        long ts = _frame->best_effort_timestamp;
        TimeSpan pts = ts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromSeconds(ts * ffmpeg.av_q2d(stream->time_base))
            : _nextAudioPts;
        _nextAudioPts = pts + TimeSpan.FromSeconds(converted / (double)_sampleRate);

        unit.IsAudio = true;
        unit.Buffer = buf;
        unit.Length = converted * _outChannels * sizeof(float);
        unit.Pts = pts;

        ffmpeg.av_frame_unref(_frame);
    }

    /// <summary>把 _frame 中的视频帧转换为 BGRA32 位图写入 unit。</summary>
    private void FillVideo(DecodedUnit unit, Func<int, byte[]> rent)
    {
        int w = _frame->width, h = _frame->height;
        if (w <= 0 || h <= 0)
        {
            ffmpeg.av_frame_unref(_frame);
            return;   // 异常帧：由调用方继续取下一帧
        }
        EnsureScaler(w, h, (AVPixelFormat)_frame->format);

        byte[] buf = rent(w * h * 4);

        // FFmpeg.AutoGen 9 的 sws_scale 接收固定尺寸托管数组（生成代码内部 fixed 固定）
        for (uint i = 0; i < 8; i++)
        {
            _srcData[i] = _frame->data[i];
            _srcStride[i] = _frame->linesize[i];
        }
        for (int i = 0; i < 4; i++) { _dstData[i] = null; _dstStride[i] = 0; }

        fixed (byte* pBuf = buf)
        {
            _dstData[0] = pBuf;
            _dstStride[0] = w * 4;
            ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, h, _dstData, _dstStride);
        }

        AVStream* stream = _fmt->streams[_videoStreamIndex];
        long ts = _frame->best_effort_timestamp;
        TimeSpan pts = ts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromSeconds(ts * ffmpeg.av_q2d(stream->time_base))
            : _nextVideoPts;
        if (_frame->duration > 0)
            _nextVideoPts = pts + TimeSpan.FromSeconds(_frame->duration * ffmpeg.av_q2d(stream->time_base));

        unit.IsAudio = false;
        unit.Buffer = buf;
        unit.Length = w * h * 4;
        unit.Width = w;
        unit.Height = h;
        unit.Stride = w * 4;
        unit.Pts = pts;

        ffmpeg.av_frame_unref(_frame);
    }

    /// <summary>惰性创建重采样器：源格式/声道布局在首帧到达时才完全确定。</summary>
    private void EnsureResampler()
    {
        if (_swr != null) return;

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, _outChannels);
        AVChannelLayout inLayout;
        if (_audioCtx->ch_layout.nb_channels > 0)
            ffmpeg.av_channel_layout_copy(&inLayout, &_audioCtx->ch_layout);
        else
            ffmpeg.av_channel_layout_default(&inLayout, 2);

        SwrContext* swr = null;
        int ret = ffmpeg.swr_alloc_set_opts2(&swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _sampleRate,
            &inLayout, _audioCtx->sample_fmt, _sampleRate,
            0, null);
        if (ret < 0 || swr == null || ffmpeg.swr_init(swr) < 0)
            throw new PlaybackException("音频重采样器初始化失败。");
        _swr = swr;
    }

    /// <summary>创建/重建像素格式转换器（源尺寸或格式变化时自动重建）。</summary>
    private void EnsureScaler(int w, int h, AVPixelFormat srcFmt)
    {
        if (_sws != null && _swsW == w && _swsH == h && _swsFmt == srcFmt) return;
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        // flags=2 即 SWS_BILINEAR（FFmpeg.AutoGen 9 未导出该常量，取 swscale.h 中的固定值）
        _sws = ffmpeg.sws_getCachedContext(null,
            w, h, srcFmt, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
            2, null, null, null);
        if (_sws == null)
            throw new PlaybackException("视频像素格式转换初始化失败。");
        _swsW = w; _swsH = h; _swsFmt = srcFmt;
        VideoWidth = w;
        VideoHeight = h;
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || _fmt == null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        // stream_index=-1：时间戳单位为 AV_TIME_BASE
        long ts = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, ts, long.MaxValue, 0);

        if (_audioCtx != null) ffmpeg.avcodec_flush_buffers(_audioCtx);
        if (_videoCtx != null) ffmpeg.avcodec_flush_buffers(_videoCtx);

        // 丢弃重采样器内残留的旧位置样本
        var swr = _swr; _swr = null;
        if (swr != null) ffmpeg.swr_free(&swr);

        // 复位发送/接收状态机
        if (_packetPending)
        {
            ffmpeg.av_packet_unref(_packet);
            _packetPending = false;
        }
        _demuxEof = false;
        _flushSent = false;
        _audioDrained = false;
        _videoDrained = false;
        _consecutiveErrors = 0;
        _nextAudioPts = position;
        _nextVideoPts = position;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var swr = _swr; _swr = null;
        if (swr != null) ffmpeg.swr_free(&swr);
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }

        var ac = _audioCtx; _audioCtx = null;
        if (ac != null) ffmpeg.avcodec_free_context(&ac);
        var vc = _videoCtx; _videoCtx = null;
        if (vc != null) ffmpeg.avcodec_free_context(&vc);

        var fmt = _fmt; _fmt = null;
        if (fmt != null) ffmpeg.avformat_close_input(&fmt);

        var frame = _frame; _frame = null;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        var packet = _packet; _packet = null;
        if (packet != null) ffmpeg.av_packet_free(&packet);
    }

    private static string? PtrToUtf8(byte* p)
        => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    private static string DescribeError(int err)
    {
        byte* buf = stackalloc byte[256];
        return ffmpeg.av_strerror(err, buf, 256) == 0
            ? Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"错误码 {err}"
            : $"错误码 {err}";
    }
}
