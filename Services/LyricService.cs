using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Utils;

namespace MusicTagRecover.Services;

/// <summary>
/// 歌词搜索/下载服务
/// 歌词源：网易云音乐、QQ音乐、酷狗音乐、酷我音乐
/// </summary>
public class LyricService : ILyricService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly ILoggerService _logger;

    // 网易云音乐 API
    // 搜索用 cloudsearch/pc（无需加密），歌词用 api/song/lyric
    private const string NeteaseSearchUrl = "https://music.163.com/api/cloudsearch/pc";
    private const string NeteaseLyricUrl = "http://music.163.com/api/song/lyric?os=pc&id={0}&lv=-1&kv=-1&tv=-1";

    // QQ音乐 API
    private const string QQSearchUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string QQLyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={0}&g_tk=5381&jsonpCallback={1}&format=jsonp";
    private const string QQLyricNewUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";

    // 酷狗音乐 API
    private const string KugouSearchUrl = "http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={0}&page={2}&pagesize={1}&showtype=1";
    private const string KugouLyricSearchUrl = "http://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={0}&hash={1}&timelength={2}&lrctxt=1";
    private const string KugouLyricDownloadUrl = "http://lyrics.kugou.com/download?ver=1&client=pc&id={0}&accesskey={1}&fmt={2}&charset=utf8";

    // 酷我音乐 API
    private const string KuwoSearchUrl = "https://search.kuwo.cn/r.s?all={0}&client=kt&pn={2}&rn={1}&ver=kwplayer_ar_9.2.3.2&vipver=1&show_copyright_off=1&newver=1&correct=1&ft=music&cluster=0&strategy=2012&encoding=utf8&rformat=json&vermerge=1&mobi=1&issubtitle=1";
    private const string KuwoLyricUrl = "https://m.kuwo.cn/newh5/singles/songinfoandlrc?musicId={0}";

    // MiniLyrics (Crintsoft)
    private const string MiniLyricsSearchUrl = "http://search.crintsoft.com/searchlyrics.htm";

    public LyricService(IHttpClientFactory httpClientFactory, ISettingsService settings, ILoggerService logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>根据源名获取带代理配置的 HttpClient</summary>
    private HttpClient GetHttpClientForSource(string source)
    {
        var proxyUrl = _settings.ProxyUrl;
        var json = _settings.ProxySourceSettings;
        var useProxy = false;

        if (!string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(proxyUrl))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (dict != null && dict.TryGetValue(source, out var enabled))
                    useProxy = enabled;
            }
            catch { /* ignore */ }
        }

        if (useProxy)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                Proxy = new WebProxy(proxyUrl),
                UseProxy = true,
            };
            return new HttpClient(handler);
        }

        return _httpClientFactory.CreateClient("default");
    }

    public async Task<IReadOnlyList<SearchResult>> SearchLyricsAsync(
        MusicFile file, SearchCondition condition, LyricInfo.DownloadConfig config,
        CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var query = condition.BuildSearchQuery(file);
        var limit = condition.WebSearchItemsLimit;
        var offset = Math.Max(0, condition.WebSearchItemsOffset);

        // 并行搜索所有歌词源，每个源使用各自的代理配置
        var sources = new Func<Task<List<SearchResult>>>[]
        {
            () => SearchNeteaseAsync(query, limit, offset, GetHttpClientForSource("netease"), ct),
            () => SearchQQMusicAsync(query, limit, offset, GetHttpClientForSource("qq"), ct),
            () => SearchKugouAsync(query, limit, offset, GetHttpClientForSource("kugou"), ct),
            () => SearchKuwoAsync(query, limit, offset, GetHttpClientForSource("kuwo"), ct),
        };

        var tasks = sources.Select(s => s());
        var allResults = await Task.WhenAll(tasks);

        foreach (var r in allResults)
            results.AddRange(r);

        // 计算匹配度并排序
        foreach (var r in results)
        {
            r.MatchScore = CalculateMatchScore(file, r);
        }

        return results
            .OrderByDescending(r => r.MatchScore)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<SearchResult>> SearchLyricsFromSourceAsync(
        MusicFile file, string source, SearchCondition condition,
        LyricInfo.DownloadConfig config, CancellationToken ct = default)
    {
        var query = condition.BuildSearchQuery(file);
        var limit = condition.WebSearchItemsLimit;
        var offset = Math.Max(0, condition.WebSearchItemsOffset);

        List<SearchResult> results;
        switch (source.ToLowerInvariant())
        {
            case "netease":
                results = await SearchNeteaseAsync(query, limit, offset, GetHttpClientForSource("netease"), ct);
                break;
            case "qq":
                results = await SearchQQMusicAsync(query, limit, offset, GetHttpClientForSource("qq"), ct);
                break;
            case "kugou":
                results = await SearchKugouAsync(query, limit, offset, GetHttpClientForSource("kugou"), ct);
                break;
            case "kuwo":
                results = await SearchKuwoAsync(query, limit, offset, GetHttpClientForSource("kuwo"), ct);
                break;
            default:
                return Array.Empty<SearchResult>();
        }

        foreach (var r in results)
            r.MatchScore = CalculateMatchScore(file, r);

        return results.OrderByDescending(r => r.MatchScore).Take(limit).ToList();
    }

    public bool SupportsPagination(string source) => source.ToLowerInvariant() switch
    {
        "netease" or "qq" or "kugou" or "kuwo" => true,
        _ => false
    };

    public async Task<LyricInfo?> DownloadLyricAsync(
        SearchResult result, LyricInfo.DownloadConfig config, CancellationToken ct = default)
    {
        // 酷狗音乐使用独立的两步下载流程，不需要 SourceUrl
        if (string.IsNullOrEmpty(result.SourceUrl) && result.SourceName != "酷狗音乐")
            return null;

        _logger.Info($"[歌词下载] 开始下载歌词: {result.Title} - {result.Artist}, 来源: {result.SourceName}");
        _logger.Debug($"[歌词下载] Debug级别日志测试");

        try
        {
            LyricInfo? lyric;

            // QQ音乐使用新API获取加密歌词并解密
            if (result.SourceName == "QQ音乐")
            {
                _logger.Debug("[歌词下载] 使用QQ音乐新API获取加密歌词");
                lyric = await DownloadQQLyricAsync(result, ct);
            }
            // 酷狗音乐需要两步：先搜索歌词候选，再下载
            else if (result.SourceName == "酷狗音乐")
            {
                _logger.Debug("[歌词下载] 使用酷狗音乐两步流程获取歌词");
                lyric = await DownloadKugouLyricAsync(result, ct);
            }
            else
            {
                string content;
                var sourceKey = GetSourceKeyFromDisplayName(result.SourceName);
                var http = GetHttpClientForSource(sourceKey);
                var request = new HttpRequestMessage(HttpMethod.Get, result.SourceUrl);
                request.Headers.Add("Referer", GetReferer(result.SourceName));
                var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                content = await response.Content.ReadAsStringAsync();

                _logger.Debug($"[歌词下载] 获取歌词内容成功，长度: {content.Length}");

                // 根据来源解析歌词
                lyric = result.SourceName switch
                {
                    "网易云音乐" => ParseNeteaseLyric(content),
                    "酷狗音乐" => ParseKugouLyric(content),
                    "酷我音乐" => ParseKuwoLyric(content),
                    _ => ParseLrcContent(content)
                };
            }

            if (lyric == null)
            {
                _logger.Debug("[歌词下载] 解析歌词失败，返回 null");
                return null;
            }

            _logger.Debug($"[歌词下载] 解析成功，原文长度: {lyric.OriginalLyric?.Length ?? 0}, 翻译长度: {lyric.TranslatedLyric?.Length ?? 0}");

            // 合并翻译歌词到原文（原文在上，翻译在下一行）
            if (config.DownloadTranslation && !string.IsNullOrEmpty(lyric.TranslatedLyric))
            {
                _logger.Debug("[歌词下载] 合并翻译歌词到原文");
                lyric.LrcFormatted = MergeLyrics(lyric.OriginalLyric, lyric.TranslatedLyric);
            }

            // 后处理
            if (config.RemoveTimetag && lyric.LrcFormatted != null)
                lyric.LrcFormatted = RemoveTimetag(lyric.LrcFormatted);

            if (config.ReformatTimetag && lyric.LrcFormatted != null)
                lyric.LrcFormatted = ReformatTimetag(lyric.LrcFormatted);

            return lyric;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[歌词下载] 下载失败: {ex.Message}");
            return null;
        }
    }

    public string ReformatTimetag(string lrcContent)
    {
        return LrcTimeTagRegex().Replace(lrcContent, match =>
        {
            var raw = match.Groups[1].Value;
            if (TimeSpan.TryParseExact(raw.Replace('.', ':'), @"m\:s\:ff", null, out var ts))
                return $"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]";
            return match.Value;
        });
    }

    public string RemoveTimetag(string lrcContent)
    {
        return LrcTimeTagRegex().Replace(lrcContent, "");
    }

    public async Task<string?> SaveLrcFileAsync(
        string directory, MusicFile file, LyricInfo lyric, LyricInfo.SaveConfig config)
    {
        var content = lyric.LrcFormatted ?? lyric.OriginalLyric ?? "";
        var encoding = ResolveLrcEncoding(config.FileDefaultEncoding);

        // 文件名：占位符替换 → 非法字符净化 → 空名兜底 → 缺扩展名补 .lrc
        var filename = config.FilenameFormat
            .Replace("{artist}", file.Artist)
            .Replace("{title}", file.Title)
            .Replace("{album}", file.Album)
            .Replace("{track}", file.Track?.ToString("D2") ?? "");

        filename = Path.GetInvalidFileNameChars()
            .Aggregate(filename, (s, c) => s.Replace(c.ToString(), ""));
        filename = filename.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(filename)))
            filename = "lyric.lrc";
        if (!Path.HasExtension(filename))
            filename += ".lrc";

        var saveDir = string.IsNullOrEmpty(config.SaveDirectory) ? directory : config.SaveDirectory;
        var path = Path.Combine(saveDir, filename);

        try
        {
            Directory.CreateDirectory(saveDir);
            await Task.Run(() => WriteLrcFileAtomic(path, content, encoding)).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[歌词保存] LRC 文件保存失败：{0}", path);
            return null;
        }
    }

    /// <summary>
    /// 按设置解析 LRC 写出编码：utf-8（无 BOM，兼容性最好）/gbk(936)/big5(950)/unicode(UTF-16LE)，
    /// 无法识别的名称一律回退 UTF-8；使用默认替换回退，生僻字符不会导致编码异常。
    /// </summary>
    public static Encoding ResolveLrcEncoding(string? encodingName)
    {
        if (!string.IsNullOrWhiteSpace(encodingName))
        {
            switch (encodingName.Trim().ToLowerInvariant())
            {
                case "gbk":
                case "gb2312":
                case "ansi":
                    return Encoding.GetEncoding(936);
                case "big5":
                    return Encoding.GetEncoding(950);
                case "unicode":
                case "utf-16":
                    return Encoding.Unicode;
                case "utf-8-bom":
                    return new UTF8Encoding(true);
            }
        }
        return new UTF8Encoding(false);
    }

    /// <summary>
    /// 原子写入文本文件：同目录临时文件写入并 Flush 后一次性替换目标文件，
    /// 避免写一半被读取到损坏内容；遇文件占用（杀毒软件扫描/播放器锁）时短重试，
    /// 遇只读属性时临时去除并在结束后恢复。自动匹配等其它保存路径也复用此方法。
    /// </summary>
    public static void WriteLrcFileAtomic(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path)!;
        var wasReadOnly = false;
        Exception? lastError = null;

        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    if (File.Exists(path))
                    {
                        var attrs = File.GetAttributes(path);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                            wasReadOnly = true;
                        }
                    }

                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var preamble = encoding.GetPreamble();
                        if (preamble.Length > 0) fs.Write(preamble, 0, preamble.Length);
                        var bytes = encoding.GetBytes(content);
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }

                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
                {
                    lastError = ex;
                    TryDeleteQuiet(tempPath);
                    Thread.Sleep(150 * attempt);
                }
                catch
                {
                    TryDeleteQuiet(tempPath);
                    throw;
                }
            }

            throw new IOException($"歌词文件写入失败（已重试 3 次）：{path}", lastError);
        }
        finally
        {
            // 无论成功或最终失败，被临时去除的只读属性都恢复（新文件也保持只读）
            if (wasReadOnly && File.Exists(path))
            {
                try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
                catch { /* 恢复失败不影响写入结果 */ }
            }
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件清理失败可忽略 */ }
    }

    public LyricInfo? ParseLrcContent(string lrcContent)
    {
        var original = new List<string>();
        var translated = new List<string>();

        foreach (var line in lrcContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.Contains("[tl:") || trimmed.Contains("【"))
                translated.Add(RemoveTimetag(trimmed));
            else
                original.Add(trimmed);
        }

        return new LyricInfo
        {
            OriginalLyric = string.Join("\n", original),
            TranslatedLyric = translated.Count > 0 ? string.Join("\n", translated) : null,
            LrcFormatted = lrcContent
        };
    }

    #region 网易云音乐 API

    private async Task<List<SearchResult>> SearchNeteaseAsync(string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        try
        {
            // cloudsearch/pc 无需加密
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("s", query),
                new KeyValuePair<string, string>("type", "1"),
                new KeyValuePair<string, string>("limit", limit.ToString()),
                new KeyValuePair<string, string>("offset", offset.ToString()),
            });
            var request = new HttpRequestMessage(HttpMethod.Post, NeteaseSearchUrl)
            {
                Content = content,
            };
            request.Headers.Add("Referer", "https://music.163.com");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36");
            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var songs = obj["result"]?.AsObject()["songs"];

            if (songs == null) return new List<SearchResult>();

            return songs.AsArray().Select(s => new SearchResult
            {
                SourceName = "网易云音乐",
                SourceUrl = string.Format(NeteaseLyricUrl, s.Get("id")),
                Title = s.Get("name")?.ToString(),
                Artist = string.Join(", ", s.Get("ar")?.AsArray().Select(a => a.Get("name")?.ToString()) ?? Array.Empty<string?>()),
                Album = s.Get("al")?.AsObject()["name"]?.ToString(),
                CoverUrl = s.Get("al")?.AsObject()["picUrl"]?.ToString()?.Replace("http://", "https://"),
                ExtraFields = new Dictionary<string, string>
                {
                    ["id"] = s.Get("id")?.ToString() ?? "",
                    ["duration"] = s.Get("duration")?.ToString() ?? ""
                }
            }).ToList();
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    private LyricInfo? ParseNeteaseLyric(string json)
    {
        try
        {
            var obj = JsonObject.Parse(json)!;
            var lrc = obj["lrc"]?.AsObject()["lyric"]?.ToString();
            var tlrc = obj["tlyric"]?.AsObject()["lyric"]?.ToString();
            var otherLrc = obj["otherLyrics"]?.ToString();

            if (string.IsNullOrEmpty(lrc)) return null;

            return new LyricInfo
            {
                OriginalLyric = lrc,
                TranslatedLyric = tlrc,
                LrcFormatted = lrc,
                SourceName = "网易云音乐"
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region QQ音乐 API

    private async Task<List<SearchResult>> SearchQQMusicAsync(string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        try
        {
            // QQ音乐使用 musicu.fcg 接口 + JSON RPC
            var postData = new Dictionary<string, string>
            {
                ["data"] = $"{{\"req_0\":{{\"method\":\"DoSearchForQQMusicDesktop\",\"module\":\"music.search.SearchCgiService\",\"param\":{{\"search_type\":0,\"query\":\"{query}\",\"page_num\":{offset / Math.Max(1, limit) + 1},\"num_per_page\":{limit}}}}}}}"
            };

            var request = new HttpRequestMessage(HttpMethod.Get, QQSearchUrl + "?" + string.Join("&", postData.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
            request.Headers.Add("Referer", "https://y.qq.com");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var songs = obj["req_0"]?.AsObject()["data"]?.AsObject()["body"]?.AsObject()["song"]?.AsObject()["list"];

            if (songs == null) return new List<SearchResult>();

            return songs.AsArray().Select(s => new SearchResult
            {
                SourceName = "QQ音乐",
                SourceUrl = string.Format(QQLyricUrl, s.Get("mid"), "MusicJsonCallback"),
                Title = s.Get("name")?.ToString(),
                Artist = s.Get("singer")?.AsArray()[0]?.AsObject()["name"]?.ToString(),
                Album = s.Get("album")?.AsObject()["name"]?.ToString(),
                CoverUrl = $"https://y.qq.com/music/photo_new/T002R800x800M000{s.Get("album")?.AsObject()["mid"]}.jpg",
                ExtraFields = new Dictionary<string, string>
                {
                    ["mid"] = s.Get("mid")?.ToString() ?? "",
                    ["songmid"] = s.Get("mid")?.ToString() ?? ""
                }
            }).ToList();
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    private LyricInfo? ParseQQLyric(string content)
    {
        try
        {
            // QQ音乐歌词返回 JSONP 格式: MusicJsonCallback({...})
            var jsonStr = JsonpRegex().Replace(content, "$1");
            var obj = JsonObject.Parse(jsonStr)!;
            var lrc = obj["lyric"]?.ToString();
            var trans = obj["trans"]?.ToString();

            if (string.IsNullOrEmpty(lrc)) return null;

            return new LyricInfo
            {
                OriginalLyric = DecodeBase64(lrc),
                TranslatedLyric = trans != null ? DecodeBase64(trans) : null,
                LrcFormatted = DecodeBase64(lrc),
                SourceName = "QQ音乐"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<LyricInfo?> DownloadQQLyricAsync(SearchResult result, CancellationToken ct)
    {
        try
        {
            var songmid = result.ExtraFields != null && result.ExtraFields.ContainsKey("songmid")
                ? result.ExtraFields["songmid"]
                : (result.ExtraFields != null && result.ExtraFields.ContainsKey("mid") ? result.ExtraFields["mid"] : null);
            if (string.IsNullOrEmpty(songmid)) return null;

            // 使用新API获取加密歌词
            var body = new JsonObject
            {
                ["comm"] = new JsonObject
                {
                    ["ct"] = "19",
                    ["cv"] = "1859",
                    ["uin"] = "0"
                },
                ["req"] = new JsonObject
                {
                    ["method"] = "GetPlayLyricInfo",
                    ["module"] = "music.musichallSong.PlayLyricInfo",
                    ["param"] = new JsonObject
                    {
                        ["format"] = "json",
                        ["crypt"] = 1,
                        ["ct"] = 19,
                        ["cv"] = 1873,
                        ["interval"] = 0,
                        ["lrc_t"] = 0,
                        ["qrc"] = 1,
                        ["qrc_t"] = 0,
                        ["roma"] = 1,
                        ["roma_t"] = 0,
                        ["songID"] = 0,
                        ["songMid"] = songmid,
                        ["trans"] = 1,
                        ["trans_t"] = 0,
                        ["type"] = -1
                    }
                }
            };

            var http = GetHttpClientForSource("qq");
            var request = new HttpRequestMessage(HttpMethod.Post, QQLyricNewUrl);
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add("Referer", "https://y.qq.com");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;

            var data = obj["req"]?.AsObject()["data"];
            if (data == null) return null;

            var lyricHex = data.Get("lyric")?.ToString();
            var transHex = data.Get("trans")?.ToString();

            if (string.IsNullOrEmpty(lyricHex)) return null;

            // 解密歌词
            var lyricText = QrcDecrypt.Decrypt(lyricHex);
            if (string.IsNullOrEmpty(lyricText)) return null;

            _logger.Debug($"[QQ音乐] 解密后原文歌词长度: {lyricText.Length}");

            // 解析原文歌词（可能是QRC逐字格式或LRC格式）
            var originalLyric = ParseQQLyricContent(lyricText);

            // 解析翻译歌词（通常是LRC格式）
            string? translatedLyric = null;
            if (!string.IsNullOrEmpty(transHex))
            {
                var transText = QrcDecrypt.Decrypt(transHex);
                if (!string.IsNullOrEmpty(transText))
                {
                    _logger.Debug($"[QQ音乐] 解密后翻译歌词长度: {transText.Length}");
                    translatedLyric = ParseQQLyricContent(transText);
                }
            }

            return new LyricInfo
            {
                OriginalLyric = originalLyric,
                TranslatedLyric = translatedLyric,
                LrcFormatted = originalLyric,
                SourceName = "QQ音乐"
            };
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// 解析QQ音乐歌词内容，自动判断是QRC逐字格式还是LRC格式
    /// </summary>
    private string ParseQQLyricContent(string content)
    {
        // 统一换行符
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // 判断是否是QRC逐字格式：包含 [数字,数字] 开头的行，或者包含 (数字,数字) 逐字时间标签
        var hasQrcTimestamp = System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\[\d+,\d+\]");
        var hasQrcCharTag = System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\(\d+,\d+\)");

        if (hasQrcTimestamp || hasQrcCharTag)
        {
            _logger.Debug("[QQ音乐] 检测到QRC逐字格式，转换为LRC");
            return ConvertQrcToLrc(normalized);
        }
        else
        {
            _logger.Debug("[QQ音乐] 检测到LRC格式，直接返回");
            return normalized.Trim();
        }
    }

    private string ConvertQrcToLrc(string qrc)
    {
        // QRC逐字歌词格式可能有两种：
        // 1. 多行格式：每行 [startMs,durationMs]文字(startMs,durationMs)文字
        // 2. 单行格式：LRC标签和QRC歌词在同一行，用空格分隔
        var result = new System.Text.StringBuilder();
        var lines = qrc.Split('\n');
        int convertedCount = 0;
        int skippedCount = 0;
        int metaCount = 0;

        _logger.Debug($"[ConvertQrcToLrc] 输入长度: {qrc.Length}, 行数: {lines.Length}");

        // 打印前3行内容用于调试
        for (int i = 0; i < Math.Min(3, lines.Length); i++)
        {
            var preview = lines[i].Length > 100 ? lines[i].Substring(0, 100) + "..." : lines[i];
            _logger.Debug($"[ConvertQrcToLrc] 第{i}行: [{preview}]");
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // 检查是否是单行格式：包含多个 [数字,数字] 或者 LRC标签和QRC歌词混在一起
            // 先按 ] 分割，处理单行格式
            var segments = System.Text.RegularExpressions.Regex.Split(trimmed, @"(?=\[)");

            foreach (var segment in segments)
            {
                var seg = segment.Trim();
                if (string.IsNullOrEmpty(seg)) continue;

                // 匹配QRC行级时间标签 [startMs,durationMs]
                var lineMatch = System.Text.RegularExpressions.Regex.Match(seg, @"^\[(\d+),(\d+)\]");
                if (lineMatch.Success)
                {
                    var lineStartMs = int.Parse(lineMatch.Groups[1].Value);
                    var lineContent = seg.Substring(lineMatch.Length);

                    // 提取所有文字，移除逐字时间标签
                    var text = System.Text.RegularExpressions.Regex.Replace(lineContent, @"\(\d+,\d+\)", "");

                    // 跳过空行
                    if (string.IsNullOrWhiteSpace(text) || text.Trim() == "//")
                    {
                        skippedCount++;
                        continue;
                    }

                    // 转换为LRC时间格式
                    var timeSpan = TimeSpan.FromMilliseconds(lineStartMs);
                    var lrcLine = $"[{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds / 10:D2}]{text.Trim()}";
                    result.AppendLine(lrcLine);
                    convertedCount++;

                    if (convertedCount <= 3)
                        _logger.Debug($"[ConvertQrcToLrc] 转换第{convertedCount}行: {lrcLine}");
                }
                else if (seg.StartsWith("[") && seg.Contains(":") && seg.EndsWith("]"))
                {
                    // LRC元数据标签，但跳过 [kana:...] 和过长的标签
                    if (!seg.StartsWith("[kana:", StringComparison.OrdinalIgnoreCase) && seg.Length < 200)
                    {
                        result.AppendLine(seg);
                        metaCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                else if (seg.StartsWith("[") && seg.Contains(":"))
                {
                    // 不完整的标签，跳过（可能是被分割的 kana 标签）
                    skippedCount++;
                }
            }
        }

        _logger.Debug($"[ConvertQrcToLrc] 转换完成: 转换{convertedCount}行, 跳过{skippedCount}行, 元数据{metaCount}行");
        return result.ToString().TrimEnd();
    }

    #endregion

    #region 酷狗音乐 API

    private async Task<List<SearchResult>> SearchKugouAsync(string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        try
        {
            var page = offset / Math.Max(1, limit) + 1;
            var url = string.Format(KugouSearchUrl, Uri.EscapeDataString(query), limit, page);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "http://www.kugou.com");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var songs = obj["data"]?.AsObject()["info"];

            if (songs == null) return new List<SearchResult>();

            return songs.AsArray().Select(s => new SearchResult
            {
                SourceName = "酷狗音乐",
                SourceUrl = "", // 歌词需要先搜索获取 hash
                Title = s.Get("songname")?.ToString(),
                Artist = s.Get("singername")?.ToString(),
                Album = s.Get("album_name")?.ToString(),
                ExtraFields = new Dictionary<string, string>
                {
                    ["hash"] = s.Get("hash")?.ToString() ?? "",
                    ["duration"] = s.Get("duration")?.ToString() ?? ""
                }
            }).ToList();
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    private async Task<LyricInfo?> DownloadKugouLyricAsync(SearchResult result, CancellationToken ct)
    {
        try
        {
            var http = GetHttpClientForSource("kugou");
            var hash = result.ExtraFields != null && result.ExtraFields.ContainsKey("hash")
                ? result.ExtraFields["hash"] : "";
            var duration = result.ExtraFields != null && result.ExtraFields.ContainsKey("duration")
                ? result.ExtraFields["duration"] : "0";

            if (string.IsNullOrEmpty(hash)) return null;

            // 第一步：搜索歌词候选
            var searchUrl = string.Format(KugouLyricSearchUrl,
                Uri.EscapeDataString(result.Title ?? ""),
                Uri.EscapeDataString(hash),
                Uri.EscapeDataString(duration));

            var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchReq.Headers.Add("KG-RC", "1");
            searchReq.Headers.Add("KG-THash", "expand_search_manager.cpp:852736169:451");
            searchReq.Headers.Add("User-Agent", "KuGou2012-9020-ExpandSearchManager");

            var searchResp = await http.SendAsync(searchReq, ct);
            var searchJson = await searchResp.Content.ReadAsStringAsync();
            var searchObj = JsonObject.Parse(searchJson)!;
            var candidates = searchObj["candidates"];

            if (candidates == null || candidates!.AsArray().Count == 0)
            {
                _logger.Debug("[酷狗音乐] 未找到歌词候选");
                return null;
            }

            // 取第一个候选
            var candidate = candidates!.AsArray()[0];
            var id = candidate.Get("id")?.ToString();
            var accessKey = candidate.Get("accesskey")?.ToString();
            var krctype = candidate.Get("krctype")?.ToString();
            var contenttype = candidate.Get("contenttype")?.ToString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey))
            {
                _logger.Debug("[酷狗音乐] 歌词候选缺少 id 或 accesskey");
                return null;
            }

            // 判断格式：krctype==1 且 contenttype!=1 为 krc，否则 lrc
            var fmt = (krctype == "1" && contenttype != "1") ? "krc" : "lrc";

            // 第二步：下载歌词
            var downloadUrl = string.Format(KugouLyricDownloadUrl,
                Uri.EscapeDataString(id),
                Uri.EscapeDataString(accessKey),
                Uri.EscapeDataString(fmt));

            var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            dlReq.Headers.Add("KG-RC", "1");
            dlReq.Headers.Add("KG-THash", "expand_search_manager.cpp:852736169:451");
            dlReq.Headers.Add("User-Agent", "KuGou2012-9020-ExpandSearchManager");

            var dlResp = await http.SendAsync(dlReq, ct);
            var dlJson = await dlResp.Content.ReadAsStringAsync();
            var dlObj = JsonObject.Parse(dlJson)!;

            var content = dlObj["content"]?.ToString();
            if (string.IsNullOrEmpty(content)) return null;

            string? lyricText;
            string? transText = null;

            if (fmt == "krc")
            {
                // KRC 格式需要解密（XOR + zlib 解压）并解析为 LRC
                var parsed = KrcDecrypt.DecryptAndParse(content);
                if (parsed == null) return null;
                lyricText = parsed.Value.lyric;
                transText = parsed.Value.tlyric;
                _logger.Debug($"[酷狗音乐] KRC解析完成, 原文行数: {lyricText?.Split('\n').Length ?? 0}, 翻译行数: {transText?.Split('\n').Length ?? 0}");
            }
            else
            {
                // LRC 格式直接 base64 解码
                lyricText = DecodeBase64(content);
            }

            if (string.IsNullOrEmpty(lyricText)) return null;

            return new LyricInfo
            {
                OriginalLyric = lyricText,
                TranslatedLyric = transText,
                LrcFormatted = lyricText,
                SourceName = "酷狗音乐"
            };
        }
        catch (Exception ex)
        {
            _logger.Debug($"[酷狗音乐] 下载歌词失败: {ex.Message}");
            return null;
        }
    }

    private LyricInfo? ParseKugouLyric(string json)
    {
        try
        {
            var obj = JsonObject.Parse(json)!;
            var lrc = obj["content"]?.ToString();

            if (string.IsNullOrEmpty(lrc)) return null;

            // 酷狗歌词是 base64 编码的
            var decoded = DecodeBase64(lrc);

            return new LyricInfo
            {
                OriginalLyric = decoded,
                LrcFormatted = decoded,
                SourceName = "酷狗音乐"
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 酷我音乐 API

    private async Task<List<SearchResult>> SearchKuwoAsync(string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        try
        {
            var page = offset / Math.Max(1, limit);
            var url = string.Format(KuwoSearchUrl, Uri.EscapeDataString(query), limit, page);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.kuwo.cn");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var songs = obj["abslist"];

            if (songs == null) return new List<SearchResult>();

            return songs.AsArray().Select(s => new SearchResult
            {
                SourceName = "酷我音乐",
                SourceUrl = string.Format(KuwoLyricUrl, s.Get("MUSICRID")?.ToString()?.Replace("MUSIC_", "")),
                Title = s.Get("SONGNAME")?.ToString(),
                Artist = s.Get("ARTIST")?.ToString(),
                Album = s.Get("ALBUM")?.ToString(),
                CoverUrl = s.Get("web_artistpic_short")?.ToString(),
                ExtraFields = new Dictionary<string, string>
                {
                    ["musicId"] = s.Get("MUSICRID")?.ToString()?.Replace("MUSIC_", "") ?? ""
                }
            }).ToList();
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    private LyricInfo? ParseKuwoLyric(string json)
    {
        try
        {
            var obj = JsonObject.Parse(json)!;
            var lrcList = obj["data"]?.AsObject()["lrclist"];

            if (lrcList == null) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var line in lrcList.AsArray())
            {
                var time = double.Parse(line.Get("time")?.ToString() ?? "0");
                var text = line.Get("lineLyric")?.ToString();
                var ts = TimeSpan.FromSeconds(time);
                sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]{text}");
            }

            var lrc = sb.ToString();

            return new LyricInfo
            {
                OriginalLyric = lrc,
                LrcFormatted = lrc,
                SourceName = "酷我音乐"
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 合并原文和翻译歌词，原文在上，翻译在下一行（按时间标签匹配）
    /// </summary>
    private static string MergeLyrics(string? original, string? translated)
    {
        if (string.IsNullOrEmpty(original)) return translated ?? "";
        if (string.IsNullOrEmpty(translated)) return original;

        // 解析翻译歌词，提取时间标签和文本
        var transDict = new Dictionary<string, string>();
        foreach (var line in translated.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var match = LrcTimeTagRegex().Match(trimmed);
            if (match.Success)
            {
                var timeTag = match.Value;
                var text = trimmed.Substring(match.Length).Trim();
                if (!string.IsNullOrEmpty(text))
                    transDict[timeTag] = text;
            }
        }

        // 合并歌词
        var result = new System.Text.StringBuilder();
        foreach (var line in original.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            result.AppendLine(trimmed);

            // 查找对应的翻译
            var match = LrcTimeTagRegex().Match(trimmed);
            if (match.Success)
            {
                var timeTag = match.Value;
                if (transDict.TryGetValue(timeTag, out var transText))
                {
                    result.AppendLine($"{timeTag}{transText}");
                }
            }
        }

        return result.ToString().TrimEnd();
    }

    private static readonly Regex LrcTimeTagRegexInstance = new(@"\[(\d{1,3}:\d{2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled);
    private static Regex LrcTimeTagRegex() => LrcTimeTagRegexInstance;

    private static double CalculateMatchScore(MusicFile file, SearchResult result)
    {
        var score = 0.0;
        var maxScore = 0.0;

        if (!string.IsNullOrEmpty(file.Title))
        {
            maxScore += 40;
            if (!string.IsNullOrEmpty(result.Title))
            {
                if (result.Title.Equals(file.Title, StringComparison.OrdinalIgnoreCase))
                    score += 40;
                else if (result.Title.IndexOf(file.Title, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 25;
                else if (file.Title.IndexOf(result.Title, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 20;
            }
        }

        if (!string.IsNullOrEmpty(file.Artist))
        {
            maxScore += 35;
            if (!string.IsNullOrEmpty(result.Artist))
            {
                if (result.Artist.Equals(file.Artist, StringComparison.OrdinalIgnoreCase))
                    score += 35;
                else if (result.Artist.IndexOf(file.Artist, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 20;
                else if (file.Artist.IndexOf(result.Artist, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 15;
            }
        }

        if (!string.IsNullOrEmpty(file.Album))
        {
            maxScore += 25;
            if (!string.IsNullOrEmpty(result.Album))
            {
                if (result.Album.Equals(file.Album, StringComparison.OrdinalIgnoreCase))
                    score += 25;
                else if (result.Album.IndexOf(file.Album, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 15;
            }
        }

        return maxScore > 0 ? score / maxScore : 0;
    }

    /// <summary>将显示名映射回源键名</summary>
    private static string GetSourceKeyFromDisplayName(string? displayName) => displayName switch
    {
        "网易云音乐" => "netease",
        "QQ音乐" => "qq",
        "酷狗音乐" => "kugou",
        "酷我音乐" => "kuwo",
        _ => "",
    };

    private static string GetReferer(string sourceName) => sourceName switch
    {
        "网易云音乐" => "http://music.163.com",
        "QQ音乐" => "https://y.qq.com",
        "酷狗音乐" => "http://www.kugou.com",
        "酷我音乐" => "https://www.kuwo.cn",
        _ => ""
    };

    private static string DecodeBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return base64;
        }
    }

    private static readonly Regex JsonpRegexInstance = new(@"^.*?\((.+)\)$", RegexOptions.Compiled);
    private static Regex JsonpRegex() => JsonpRegexInstance;

    #endregion
}
