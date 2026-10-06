using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text.RegularExpressions;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.Utils;

namespace MusicTagRecover.Services;

/// <summary>
/// 封面图片服务
/// 支持多个在线源搜索封面：
///   - iTunes (itunes.apple.com)
///   - Last.fm (ws.audioscrobbler.com)
///   - 网易云音乐 (music.163.com)
///   - QQ音乐 (c.y.qq.com)
///   - 酷我音乐 (kuwo.cn)
///   - MusicBrainz (musicbrainz.org + coverartarchive.org)
///   - Discogs (api.discogs.com)
/// </summary>
public class CoverService : ICoverService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly ILoggerService _logger;
    private readonly IImageCache _imageCache;

    // ======== 源名称常量 ========
    public const string SourceDefault = "default";
    public const string SourceNetease = "netease";
    public const string SourceQQ = "qq";
    public const string SourceITunes = "itunes";
    public const string SourceKuwo = "kuwo";
    public const string SourceLastfm = "lastfm";
    public const string SourceMusicBrainz = "musicbrainz";
    public const string SourceDiscogs = "discogs";

    // ======== iTunes ========
    private const string ItunesSearchUrl =
        "https://itunes.apple.com/search?term={0}&media=music&entity=song&limit={1}&offset={2}&country={3}";

    // ======== 网易云音乐 ========
    private const string NeteaseSearchUrl =
        "https://music.163.com/api/cloudsearch/pc";
    private const string NeteaseSongDetailUrl =
        "http://music.163.com/api/song/detail/";
    private const string NeteaseLyricUrl =
        "http://music.163.com/api/song/lyric";

    // ======== QQ音乐 ========
    // QQ 专辑封面 URL
    private const string QqCoverUrl =
        "https://y.gtimg.cn/music/photo_new/T002R800x800M000{0}.jpg";
    private const string QqSearchUrl =
        "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?key={0}&format=json";

    // ======== 酷我音乐 ========
    private const string KuwoSearchUrl =
        "https://search.kuwo.cn/r.s?all={0}&client=kt&pn={2}&rn={1}&ver=kwplayer_ar_9.2.3.2&vipver=1&show_copyright_off=1&newver=1&correct=1&ft=music&cluster=0&strategy=2012&encoding=utf8&rformat=json&vermerge=1&mobi=1";

    // ======== Last.fm ========
    private const string LastfmApiKey = "09c55292403d961aa517ff7f5e8a3d9c";
    private const string LastfmSearchUrl =
        "https://ws.audioscrobbler.com/2.0/?method=album.search&album={0}&api_key={1}&format=json&limit={2}";
    private const string LastfmGetInfoUrl =
        "https://ws.audioscrobbler.com/2.0/?method=album.getinfo&api_key={0}&artist={1}&album={2}&format=json&autocorrect=1";
    private const string LastfmNoImageHash = "2a96cbd8b46e8629b05392ea4b2c726b";

    // ======== MusicBrainz ========
    private const string MbBaseUrl = "https://musicbrainz.org/ws/2/";
    private const string MbReleaseSearchUrl =
        MbBaseUrl + "release?query={0}&fmt=json&limit={1}&offset={2}";
    private const string MbRecordingSearchUrl =
        MbBaseUrl + "recording?query={0}&fmt=json&limit={1}&offset={2}";
    private const string MbReleaseDetailUrl =
        MbBaseUrl + "release/{0}?fmt=json&inc=artist-credits+recordings";
    private const string CoverArtArchiveUrl =
        "https://coverartarchive.org/release/{0}/front";

    // ======== Discogs ========
    private const string DiscogsSearchUrl =
        "https://api.discogs.com/database/search?q={0}&type=release&per_page={1}&page={2}";
    private const string DiscogsReleaseDetailUrl =
        "https://api.discogs.com/releases/{0}";

    public CoverService(IHttpClientFactory httpClientFactory, ISettingsService settings,
        ILoggerService logger, IImageCache imageCache)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _imageCache = imageCache;
    }

    /// <summary>根据源名获取带代理配置的 HttpClient</summary>
    public HttpClient CreateHttpClientForSource(string sourceDisplayName)
    {
        var sourceKey = GetSourceKeyFromDisplayName(sourceDisplayName);
        return GetHttpClientForSource(sourceKey);
    }

    /// <summary>下载图片字节（带 URL→文件缓存，同 URL 只下载一次）。字节落入 cache\img\。</summary>
    public async Task<byte[]?> DownloadImageBytesAsync(string imageUrl, string sourceDisplayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;

        // 下载逻辑封装成 fetcher 委托，IImageCache 负责缓存命中/写入/索引
        return await _imageCache.GetOrDownloadAsync(imageUrl, async token =>
        {
            using var http = CreateHttpClientForSource(sourceDisplayName);
            http.Timeout = TimeSpan.FromSeconds(20);

            try
            {
                var shortUrl = imageUrl.Length > 80 ? imageUrl.Substring(0, 80) : imageUrl;
                _logger.Debug($"[CoverService] 下载图片: {shortUrl}...");

                var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                // 根据源设置 Referer
                if (sourceDisplayName.Contains("163") || sourceDisplayName.Contains("netease"))
                    request.Headers.Add("Referer", "https://music.163.com");
                else if (sourceDisplayName.Contains("qq") || sourceDisplayName.Contains("QQ"))
                    request.Headers.Add("Referer", "https://y.qq.com");
                else if (sourceDisplayName.Contains("kuwo") || sourceDisplayName.Contains("酷我"))
                    request.Headers.Add("Referer", "https://kuwo.cn");
                else if (sourceDisplayName.Contains("last") || sourceDisplayName.Contains("Last"))
                    request.Headers.Add("Referer", "https://www.last.fm");
                else
                    request.Headers.Add("Referer", "https://music.163.com");

                var response = await http.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                var shortUrl = imageUrl.Length > 80 ? imageUrl.Substring(0, 80) : imageUrl;
                _logger.Error(ex, $"[CoverService] 下载图片失败: {shortUrl}...");
                return null;
            }
        }, ct);
    }

    /// <summary>根据源 key 获取带代理配置的 HttpClient（内部使用）</summary>
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

        _logger.Debug($"[CoverService] GetHttpClientForSource: source={source}, useProxy={useProxy}, proxyUrl={proxyUrl}, proxySettings={json ?? "null"}");

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

    // ============================================================
    // 公共接口实现
    // ============================================================

    /// <summary>搜索封面图片（聚合所有源）</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchCoversAsync(
        MusicFile file, SearchCondition condition, CancellationToken ct = default)
    {
        return await SearchCoversFromSourceAsync(file, SourceDefault, condition, ct);
    }

    /// <summary>从指定数据源搜索封面图片</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchCoversFromSourceAsync(
        MusicFile file, string source, SearchCondition condition, CancellationToken ct = default)
    {
        var query = condition.BuildSearchQuery(file);
        var limit = condition.WebSearchItemsLimit;
        var offset = Math.Max(0, condition.WebSearchItemsOffset);
        var country = condition.ItunesCountry ?? _settings.ItunesSearchParamsCountry ?? "US";

        List<SearchResult> results;
        switch (source.ToLowerInvariant())
        {
            case SourceDefault:
                // 聚合所有源，每个源使用各自的代理配置
                var tasks = new[]
                {
                    SearchITunesAsync(query, limit, offset, country, GetHttpClientForSource(SourceITunes), ct),
                    SearchNeteaseAsync(query, limit, offset, GetHttpClientForSource(SourceNetease), ct),
                    SearchQQAsync(query, limit, offset, GetHttpClientForSource(SourceQQ), ct),
                    SearchKuwoAsync(query, limit, offset, GetHttpClientForSource(SourceKuwo), ct),
                    SearchLastfmAsync(file, limit, offset, GetHttpClientForSource(SourceLastfm), ct),
                    SearchMusicBrainzCoverAsync(query, limit, offset, GetHttpClientForSource(SourceMusicBrainz), ct),
                    SearchDiscogsCoverAsync(query, limit, offset, GetHttpClientForSource(SourceDiscogs), ct),
                };
                var all = await Task.WhenAll(tasks);
                results = all.SelectMany(r => r).ToList();
                break;

            case SourceITunes:
                results = await SearchITunesAsync(query, limit, offset, country, GetHttpClientForSource(SourceITunes), ct);
                break;
            case SourceNetease:
                results = await SearchNeteaseAsync(query, limit, offset, GetHttpClientForSource(SourceNetease), ct);
                break;
            case SourceQQ:
                results = await SearchQQAsync(query, limit, offset, GetHttpClientForSource(SourceQQ), ct);
                break;
            case SourceKuwo:
                results = await SearchKuwoAsync(query, limit, offset, GetHttpClientForSource(SourceKuwo), ct);
                break;
            case SourceLastfm:
                results = await SearchLastfmAsync(file, limit, offset, GetHttpClientForSource(SourceLastfm), ct);
                break;
            case SourceMusicBrainz:
                results = await SearchMusicBrainzCoverAsync(query, limit, offset, GetHttpClientForSource(SourceMusicBrainz), ct);
                break;
            case SourceDiscogs:
                results = await SearchDiscogsCoverAsync(query, limit, offset, GetHttpClientForSource(SourceDiscogs), ct);
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
        SourceDefault => true,
        SourceNetease or SourceQQ or SourceKuwo or SourceLastfm => true,
        // iTunes currently returns the same first page when offset is supplied.
        // Keep its manual "load more" button hidden until the endpoint paginates.
        SourceITunes => false,
        SourceMusicBrainz or SourceDiscogs => true,
        _ => false
    };

    public async Task<IReadOnlyList<SearchResult>> SearchTagsFromSourceAsync(
        MusicFile file, string source, SearchCondition condition,
        CancellationToken ct = default)
    {
        var query = condition.BuildSearchQuery(file);
        switch (source.ToLowerInvariant())
        {
            case SourceMusicBrainz:
                return await SearchMusicBrainzTagsAsync(query, condition.WebSearchItemsLimit,
                    Math.Max(0, condition.WebSearchItemsOffset), ct);
            case SourceDiscogs:
                return await SearchDiscogsTagsAsync(query, condition.WebSearchItemsLimit,
                    Math.Max(0, condition.WebSearchItemsOffset), ct);
            default:
                return await SearchCoversFromSourceAsync(file, source, condition, ct);
        }
    }

    public async Task<CoverDownloadResult?> DownloadCoverAsync(
        SearchResult result, CoverArt.LimitsConfig limits, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.CoverUrl))
            return new CoverDownloadResult { ValidationError = "无封面URL" };

        try
        {
            _logger.Debug($"[CoverService] 下载封面: source={result.SourceName}, originalUrl={result.CoverUrl}");

            // 酷我的 CoverUrl 是 pic API 端点，需要先解析获取实际图片 URL
            var imageUrl = result.CoverUrl;
            var source = result.SourceName?.ToLowerInvariant() ?? "";
            if ((source.Contains("kuwo") || source.Contains("酷我")) && result.CoverUrl.Contains("artistpicserver.kuwo.cn"))
            {
                _logger.Debug($"[CoverService] Kuwo: 解析 pic.web URL...");
                var http = CreateHttpClientForSource(result.SourceName ?? "");
                imageUrl = await ResolveKuwoCoverUrlAsync(result.CoverUrl, http, ct);
                _logger.Debug($"[CoverService] Kuwo: 解析结果 = {imageUrl ?? "null"}");
                if (string.IsNullOrEmpty(imageUrl)) return null;
            }

            // 使用带缓存的下载
            var data = await DownloadImageBytesAsync(imageUrl, result.SourceName ?? "", ct);
            if (data == null || data.Length == 0) return null;

            var cover = new CoverArt
            {
                ImageData = data,
                MimeType = GetMimeTypeFromUrl(imageUrl)
            };

            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms);
            cover.Width = img.Width;
            cover.Height = img.Height;

            if (!cover.Validate(limits, out var validationError))
            {
                _logger.Debug($"[CoverService] 封面验证失败: {validationError}");
                return new CoverDownloadResult { ValidationError = validationError };
            }

            _logger.Debug($"[CoverService] 封面下载成功: {cover.Width}x{cover.Height}, {cover.FileSizeBytes} bytes");
            return new CoverDownloadResult { Cover = cover };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[CoverService] 封面下载异常: source={result.SourceName}, url={result.CoverUrl}");
            return new CoverDownloadResult { ValidationError = $"下载异常：{ex.Message}" };
        }
    }

    /// <summary>酷我 pic API 返回实际图片 URL（如 http://...jpg），需要先解析再下载</summary>
    private static async Task<string?> ResolveKuwoCoverUrlAsync(string picApiUrl, HttpClient http, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, picApiUrl);
            request.Headers.Add("Referer", "https://kuwo.cn");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/68.0.3440.106 Safari/537.36");
            var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            // 返回的是纯文本 URL，可能以 http 或 https 开头
            body = body.Trim();
            if (body.StartsWith("http://") || body.StartsWith("https://"))
            {
                // 优先升级为 HTTPS（kwcdn 域名不支持 HTTPS，但 kuwo.cn 域名支持）
                if (body.StartsWith("http://"))
                    body = body.Replace("http://img1.kwcdn.kuwo.cn/", "https://img1.kuwo.cn/")
                               .Replace("http://img2.kwcdn.kuwo.cn/", "https://img2.kuwo.cn/")
                               .Replace("http://img3.kwcdn.kuwo.cn/", "https://img3.kuwo.cn/")
                               .Replace("http://img4.kwcdn.kuwo.cn/", "https://img4.kuwo.cn/");
                return body;
            }
            return null;
        }
        catch { return null; }
    }

    public CoverArt? CompressCover(CoverArt cover,
        int maxWidth = 500, int maxHeight = 500, int quality = 85)
    {
        if (!cover.HasImage) return null;

        try
        {
            using var ms = new MemoryStream(cover.ImageData!);
            using var img = Image.FromStream(ms);

            var ratio = Math.Min((double)maxWidth / img.Width, (double)maxHeight / img.Height);
            if (ratio >= 1.0) return cover;

            var newWidth = (int)(img.Width * ratio);
            var newHeight = (int)(img.Height * ratio);

            using var bitmap = new Bitmap(newWidth, newHeight);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(img, 0, 0, newWidth, newHeight);

            using var outMs = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(e => e.MimeType == "image/jpeg");
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

            var codec = encoder ?? GetEncoder("image/jpeg")
                ?? throw new InvalidOperationException("系统未提供 JPEG 图像编码器");
            bitmap.Save(outMs, codec, encoderParams);

            return new CoverArt
            {
                ImageData = outMs.ToArray(),
                MimeType = "image/jpeg",
                Width = newWidth,
                Height = newHeight
            };
        }
        catch
        {
            return null;
        }
    }

    public bool ValidateCover(CoverArt cover, CoverArt.LimitsConfig limits, out string errorMessage)
        => cover.Validate(limits, out errorMessage);

    public CoverArt? LoadImageFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            using var img = Image.FromFile(filePath);
            return new CoverArt
            {
                ImageData = File.ReadAllBytes(filePath),
                MimeType = GetMimeTypeFromExtension(Path.GetExtension(filePath)),
                Width = img.Width,
                Height = img.Height
            };
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // 各源搜索实现
    // ============================================================

    /// <summary>iTunes 搜索</summary>
    private async Task<List<SearchResult>> SearchITunesAsync(
        string query, int limit, int offset, string country, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var url = string.Format(ItunesSearchUrl,
                Uri.EscapeDataString(query), limit, offset, Uri.EscapeDataString(country));
            var response = await http.GetAsync(url, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var items = obj["results"];
            if (items != null)
            {
                foreach (var item in items.AsArray())
                {
                    if (item == null) continue;
                    // iTunes CDN supports dynamic resolution — request 3000x3000
                    var rawUrl = item.Get("artworkUrl100")?.ToString();
                    var coverUrl = rawUrl;
                    if (!string.IsNullOrEmpty(coverUrl))
                    {
                        var lastSlash = coverUrl.LastIndexOf('/');
                        if (lastSlash >= 0)
                        {
                            var prefix = coverUrl.Substring(0, lastSlash + 1);
                            var filename = coverUrl.Substring(lastSlash + 1);
                            filename = System.Text.RegularExpressions.Regex.Replace(
                                filename, @"(\d+)x(\d+)", "3000x3000");
                            coverUrl = prefix + filename;
                        }
                    }
                    if (!string.IsNullOrEmpty(coverUrl))
                    {
                        var releaseDate = item.Get("releaseDate")?.ToString();
                        var sr = new SearchResult
                        {
                            SourceName = "iTunes",
                            SourceUrl = coverUrl,
                            Title = item.Get("trackName")?.ToString(),
                            Artist = item.Get("artistName")?.ToString(),
                            Album = item.Get("collectionName")?.ToString(),
                            Year = releaseDate is { Length: >= 4 } ? releaseDate.Substring(0, 4) : null,
                            CoverUrl = coverUrl,
                            ExtraFields = new Dictionary<string, string>
                            {
                                ["itunesTrackId"] = item.Get("trackId")?.ToString() ?? "",
                            }
                        };
                        var trackToken = item.Get("trackNumber");
                        if (trackToken != null)
                            sr.ExtraFields["track"] = trackToken.ToString();
                        var discToken = item.Get("discNumber");
                        if (discToken != null)
                            sr.ExtraFields["disc"] = discToken.ToString();
                        results.Add(sr);
                    }
                }
            }
        }
        catch { /* ignore */ }
        return results;
    }

    /// <summary>网易云音乐搜索（cloudsearch/pc 无需加密）</summary>
    private async Task<List<SearchResult>> SearchNeteaseAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
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
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36");
            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;

            // cloudsearch/pc 返回: result.songs[].al.{id, name, picUrl}
            var songs = obj["result"]?.AsObject()["songs"];
            if (songs != null)
            {
                foreach (var song in songs.AsArray())
                {
                    if (song == null) continue;
                    var album = song.Get("al");
                    var picUrl = album?.AsObject()["picUrl"]?.ToString();
                    if (!string.IsNullOrEmpty(picUrl))
                    {
                        picUrl = picUrl.Replace("http://", "https://");
                        var sr = new SearchResult
                        {
                            SourceName = "网易云音乐",
                            SourceUrl = picUrl,
                            Title = song.Get("name")?.ToString(),
                            Artist = string.Join(", ", song.Get("ar")?.AsArray().Select(a => a.Get("name")?.ToString()) ?? Array.Empty<string?>()),
                            Album = album?.AsObject()["name"]?.ToString(),
                            Year = GetNeteaseYear(song),
                            CoverUrl = picUrl,
                        };
                        var trackToken = song.Get("no");
                        if (trackToken != null && trackToken.NotNull())
                            sr.ExtraFields["track"] = trackToken.ToString();
                        results.Add(sr);
                    }
                }
            }
        }
        catch { /* ignore */ }
        return results;
    }

    /// <summary>QQ 音乐搜索</summary>
    private async Task<List<SearchResult>> SearchQQAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            // smartbox 没有页码；首轮保留原有结果，后续页使用支持 page 的搜索端点。
            if (offset > 0)
                return await SearchQQPagedAsync(query, limit, offset, http, ct);

            // 1. 搜索获取 album mid
            var searchUrl = string.Format(QqSearchUrl, Uri.EscapeDataString(query));
            var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Add("Referer", "https://y.qq.com");
            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;

            // smartbox 返回: data.song.itemlist[].mid, album.mid, album.name, singer
            var songList = obj["data"]?.AsObject()["song"]?.AsObject()["itemlist"];
            if (songList != null)
            {
                var count = 0;
                foreach (var item in songList.AsArray())
                {
                    if (item == null) continue;
                    if (count >= limit) break;
                    var albumMid = item.Get("album")?.AsObject()["mid"]?.ToString();
                    if (string.IsNullOrEmpty(albumMid)) continue;

                    var coverUrl = string.Format(QqCoverUrl, albumMid);
                    var singerToken = item.Get("singer");
                    var artists = singerToken != null
                        ? string.Join(", ", singerToken.AsArray().Select(s => s.Get("name")?.ToString()))
                        : "";

                    results.Add(new SearchResult
                    {
                        SourceName = "QQ音乐",
                        SourceUrl = coverUrl,
                        Title = item.Get("name")?.ToString(),
                        Artist = artists,
                        Album = item.Get("album")?.AsObject()["name"]?.ToString(),
                        CoverUrl = coverUrl,
                    });
                    count++;
                }
            }

            // 如果 smartbox 没结果，尝试用 splcloud 搜索
            if (results.Count == 0)
            {
                var splUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p=1&n={limit}";
                var req2 = new HttpRequestMessage(HttpMethod.Get, splUrl);
                req2.Headers.Add("Referer", "https://y.qq.com");
                var resp2 = await http.SendAsync(req2, ct);
                var json2 = await resp2.Content.ReadAsStringAsync();
                var obj2 = JsonObject.Parse(json2)!;
                var songs2 = obj2["data"]?.AsObject()["song"]?.AsObject()["list"];
                if (songs2 != null)
                {
                    foreach (var song in songs2.AsArray())
                    {
                        if (song == null) continue;
                        var albumMid = song.Get("albummid")?.ToString();
                        if (string.IsNullOrEmpty(albumMid)) continue;

                        var coverUrl = string.Format(QqCoverUrl, albumMid);
                        var singerToken = song.Get("singer");
                        var splArtists = singerToken != null
                            ? string.Join(", ", singerToken.AsArray().Select(s => s.Get("name")?.ToString()))
                            : "";
                        var yearStr = song.Get("year")?.ToString();
                        results.Add(new SearchResult
                        {
                            SourceName = "QQ音乐",
                            SourceUrl = coverUrl,
                            Title = song.Get("songname")?.ToString(),
                            Artist = splArtists,
                            Album = song.Get("albumname")?.ToString(),
                            Year = yearStr is { Length: >= 4 } ? yearStr.Substring(0, 4) : null,
                            CoverUrl = coverUrl,
                        });
                    }
                }
            }
        }
        catch { /* ignore */ }
        return results;
    }

    private async Task<List<SearchResult>> SearchQQPagedAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var page = offset / Math.Max(1, limit) + 1;
        var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p={page}&n={limit}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", "https://y.qq.com");
        var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync();
        var obj = JsonObject.Parse(json)!;
        var songs = obj["data"]?.AsObject()["song"]?.AsObject()["list"];
        if (songs == null) return results;

        foreach (var song in songs.AsArray())
        {
            var albumMid = song.Get("albummid")?.ToString();
            if (string.IsNullOrEmpty(albumMid)) continue;
            var coverUrl = string.Format(QqCoverUrl, albumMid);
            results.Add(new SearchResult
            {
                SourceName = "QQ音乐",
                SourceUrl = coverUrl,
                Title = song.Get("songname")?.ToString(),
                Artist = song.Get("singer") != null
                    ? string.Join(", ", song.Get("singer")!.AsArray().Select(s => s.Get("name")?.ToString())) : "",
                Album = song.Get("albumname")?.ToString(),
                Year = song.Get("year")?.ToString()?.Length >= 4
                    ? song.Get("year")!.ToString().Substring(0, 4) : null,
                CoverUrl = coverUrl,
            });
        }
        return results;
    }

    /// <summary>酷我音乐搜索</summary>
    private async Task<List<SearchResult>> SearchKuwoAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var page = offset / Math.Max(1, limit);
            var url = string.Format(KuwoSearchUrl, Uri.EscapeDataString(query), limit, page);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://kuwo.cn");
            var response = await http.SendAsync(request, ct);

            _logger.Debug($"[CoverService] Kuwo 搜索 HTTP: {(int)response.StatusCode}, 查询: {query}");

            var json = await response.Content.ReadAsStringAsync();
            _logger.Debug($"[CoverService] Kuwo 搜索响应长度: {json.Length} 字节");

            // Kuwo 返回的 JSON: abslist[].MUSICRID, NAME, ARTIST, ALBUM
            var obj = JsonObject.Parse(json)!;
            var abslist = obj["abslist"];
            if (abslist != null)
            {
                foreach (var item in abslist.AsArray())
                {
                    if (item == null) continue;
                    var musicId = item.Get("MUSICRID")?.ToString() ?? "";
                    var rid = Regex.Replace(musicId, "^MUSIC_", "");

                    // 优先使用 web_albumpic_short 构建专辑封面直达 URL（HTTPS）
                    var coverUrl = BuildKuwoCoverUrl(item);
                    var coverType = string.IsNullOrEmpty(coverUrl) ? "pic.web" : "direct";
                    if (string.IsNullOrEmpty(coverUrl))
                    {
                        // 回退到 pic.web 重定向端点
                        coverUrl = $"http://artistpicserver.kuwo.cn/pic.web?corp=kuwo&type=rid_pic&pictype=500&size=500&rid={rid}";
                    }

                    _logger.Debug($"[CoverService] Kuwo 封面[{coverType}]: {coverUrl}, Title={item.Get("NAME")}");

                    // SourceUrl 保存原始来源 URL（pic.web 端点），CoverUrl 保存实际图片地址
                    var sourceUrl = $"http://artistpicserver.kuwo.cn/pic.web?corp=kuwo&type=rid_pic&pictype=500&size=500&rid={rid}";

                    var releaseDate = item.Get("RELEASEDATE")?.ToString();
                    var sr = new SearchResult
                    {
                        SourceName = "酷我音乐",
                        SourceUrl = sourceUrl,
                        Title = item.Get("NAME")?.ToString() ?? item.Get("SONGNAME")?.ToString(),
                        Artist = item.Get("ARTIST")?.ToString() ?? "",
                        Album = item.Get("ALBUM")?.ToString() ?? "",
                        Year = releaseDate is { Length: >= 4 } ? releaseDate.Substring(0, 4) : null,
                        CoverUrl = coverUrl,
                    };
                    var trackToken = item.Get("TRACK_NUMBER");
                    if (trackToken != null)
                        sr.ExtraFields["track"] = trackToken.ToString();
                    results.Add(sr);
                }
            }

            _logger.Debug($"[CoverService] Kuwo 搜索结果: {results.Count} 条");
        }
        catch (Exception ex) { _logger.Error(ex, "[CoverService] Kuwo 搜索异常"); }
        return results;
    }

    /// <summary>从 Kuwo 搜索结果构建封面图片直达 URL</summary>
    /// <remarks>
    /// web_albumpic_short 格式："120/s4s81/2/3200337129.jpg"
    /// 去掉尺寸前缀后拼接为 HTTPS CDN 地址，比 pic.web 重定向更可靠。
    /// </remarks>
    private static string? BuildKuwoCoverUrl(JsonNode item)
    {
        var albumpicShort = item.Get("web_albumpic_short")?.ToString();
        if (!string.IsNullOrEmpty(albumpicShort))
        {
            // 去掉尺寸前缀（如 "120/"），得到路径 "s4s81/2/3200337129.jpg"
            var path = albumpicShort;
            var slashIdx = path.IndexOf('/');
            if (slashIdx >= 0)
                path = path.Substring(slashIdx + 1);

            if (!string.IsNullOrEmpty(path))
                return $"https://img1.kuwo.cn/star/albumcover/500/{path}";
        }

        // 次选 hts_MVPIC（MV 封面，HTTPS）
        var htsMvpic = item.Get("hts_MVPIC")?.ToString();
        if (!string.IsNullOrEmpty(htsMvpic))
            return htsMvpic;

        // 再次选 MVPIC（MV 封面路径，HTTP）
        var mvpic = item.Get("MVPIC")?.ToString();
        if (!string.IsNullOrEmpty(mvpic))
            return $"http://img1.kuwo.cn/wmvpic/500/{mvpic}";

        return null;
    }

    /// <summary>Last.fm 搜索封面</summary>
    /// <remarks>
    /// 1. album.search + artist + album（分离参数，最可靠）
    /// 2. album.search + album only（无 artist 时回退）
    /// 3. track.search + track + artist（无结果时回退）
    ///
    /// 图片尺寸优先级：mega → extralarge → large
    /// 过滤掉 noimage hash 占位图。
    /// </remarks>
    private async Task<List<SearchResult>> SearchLastfmAsync(
        MusicFile file, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        _logger.Debug($"[CoverService] Last.fm 搜索开始: Artist={file.Artist}, Album={file.Album}, Title={file.Title}");

        // 策略1：album.search + artist + album
        if (!string.IsNullOrEmpty(file.Album))
        {
            _logger.Debug($"[CoverService] Last.fm 策略1: album.search + artist + album");
            results = await SearchLastfmAlbumAsync(file.Album, file.Artist, limit, offset, http, ct);
            _logger.Debug($"[CoverService] Last.fm 策略1 结果: {results.Count} 条");
            if (results.Count > 0) return results;
        }

        // 策略2：album.search + album only
        if (!string.IsNullOrEmpty(file.Album))
        {
            _logger.Debug($"[CoverService] Last.fm 策略2: album.search (仅 album)");
            results = await SearchLastfmAlbumAsync(file.Album, null, limit, offset, http, ct);
            _logger.Debug($"[CoverService] Last.fm 策略2 结果: {results.Count} 条");
            if (results.Count > 0) return results;
        }

        // 策略3：track.search + track + artist
        if (!string.IsNullOrEmpty(file.Title))
        {
            _logger.Debug($"[CoverService] Last.fm 策略3: track.search + track + artist");
            results = await SearchLastfmTrackAsync(file.Title, file.Artist, limit, offset, http, ct);
            _logger.Debug($"[CoverService] Last.fm 策略3 结果: {results.Count} 条");
        }

        _logger.Debug($"[CoverService] Last.fm 搜索结束: 共 {results.Count} 条结果");
        return results;
    }

    /// <summary>Last.fm album.search（分离 album + artist 参数）</summary>
    private async Task<List<SearchResult>> SearchLastfmAlbumAsync(
        string album, string? artist, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("&album=").Append(Uri.EscapeDataString(album));
            if (!string.IsNullOrEmpty(artist))
                sb.Append("&artist=").Append(Uri.EscapeDataString(artist));
            var page = offset / Math.Max(1, limit) + 1;
            var url = $"https://ws.audioscrobbler.com/2.0/?limit={limit}&page={page}&format=json&api_key={LastfmApiKey}&method=album.search{sb}";

            _logger.Debug($"[CoverService] Last.fm album.search URL: {url}");

            var response = await http.GetAsync(url, ct);
            _logger.Debug($"[CoverService] Last.fm album.search HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync();
            _logger.Debug($"[CoverService] Last.fm album.search 响应长度: {json.Length}");

            var obj = JsonObject.Parse(json)!;

            var albums = obj["results"]?.AsObject()["albummatches"]?.AsObject()["album"];
            if (albums == null) { _logger.Debug("[CoverService] Last.fm album.search: album 节点为 null"); return results; }

            foreach (var albumItem in albums.AsArray())
            {
                var name = albumItem.Get("name")?.ToString() ?? "";
                var artistName = albumItem.Get("artist")?.ToString() ?? "";
                var image = albumItem.Get("image");
                if (image == null || image.AsArray().Count == 0) continue;

                var coverUrl = GetLastfmBestImage(image);
                if (string.IsNullOrEmpty(coverUrl)) continue;

                _logger.Debug($"[CoverService] Last.fm album.search 封面: {coverUrl}");
                results.Add(new SearchResult
                {
                    SourceName = "Last.fm",
                    SourceUrl = coverUrl,
                    Title = name,
                    Artist = artistName,
                    Album = name,
                    CoverUrl = coverUrl,
                });
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[CoverService] Last.fm album.search 异常"); }
        return results;
    }

    /// <summary>Last.fm track.search（分离 track + artist 参数）</summary>
    private async Task<List<SearchResult>> SearchLastfmTrackAsync(
        string track, string? artist, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("&track=").Append(Uri.EscapeDataString(track));
            if (!string.IsNullOrEmpty(artist))
                sb.Append("&artist=").Append(Uri.EscapeDataString(artist));
            var page = offset / Math.Max(1, limit) + 1;
            var url = $"https://ws.audioscrobbler.com/2.0/?limit={limit}&page={page}&format=json&api_key={LastfmApiKey}&method=track.search{sb}";

            _logger.Debug($"[CoverService] Last.fm track.search URL: {url}");

            var response = await http.GetAsync(url, ct);
            _logger.Debug($"[CoverService] Last.fm track.search HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync();
            _logger.Debug($"[CoverService] Last.fm track.search 响应长度: {json.Length}");

            var obj = JsonObject.Parse(json)!;

            var tracks = obj["results"]?.AsObject()["trackmatches"]?.AsObject()["track"];
            if (tracks == null) { _logger.Debug("[CoverService] Last.fm track.search: track 节点为 null"); return results; }

            foreach (var trackItem in tracks.AsArray())
            {
                var name = trackItem.Get("name")?.ToString() ?? "";
                var artistName = trackItem.Get("artist")?.ToString() ?? "";
                var image = trackItem.Get("image");
                if (image == null || image.AsArray().Count == 0) continue;

                var coverUrl = GetLastfmBestImage(image);
                if (string.IsNullOrEmpty(coverUrl)) continue;

                results.Add(new SearchResult
                {
                    SourceName = "Last.fm",
                    SourceUrl = coverUrl,
                    Title = name,
                    Artist = artistName,
                    Album = "",
                    CoverUrl = coverUrl,
                });
            }
        }
        catch (Exception ex) { _logger.Error(ex, "[CoverService] Last.fm track.search 异常"); }
        return results;
    }

    /// <summary>从 Last.fm image 数组中提取最佳封面 URL</summary>
    /// <param name="imageArray">Last.fm image JSON array: [small, medium, large, extralarge, mega]</param>
    /// <returns>最佳图片 URL，无有效图片返回 null</returns>
    private static string? GetLastfmBestImage(JsonNode? imageArray)
    {
        if (imageArray is not JsonArray arr || arr.Count == 0) return null;

        // 构建 size→url 字典
        var dict = new Dictionary<string, string>();
        foreach (var img in arr)
        {
            var size = img.Get("size")?.ToString();
            var url = img.Get("#text")?.ToString();
            if (!string.IsNullOrEmpty(size) && !string.IsNullOrEmpty(url) && !dict.ContainsKey(size))
                dict[size] = url;
        }

        // 优先级：mega → extralarge → large → medium
        string? best = null;
        if (dict.TryGetValue("mega", out best)) return best;
        if (dict.TryGetValue("extralarge", out best) && !best.Contains(LastfmNoImageHash)) return best;
        if (dict.TryGetValue("large", out best) && !best.Contains(LastfmNoImageHash)) return best;
        if (dict.TryGetValue("medium", out best) && !best.Contains(LastfmNoImageHash)) return best;

        // 回退：按序查找任意不含 noimage 的图片
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            var url = arr[i].Get("#text")?.ToString();
            if (!string.IsNullOrEmpty(url) && !url.Contains(LastfmNoImageHash))
                return url;
        }
        return null;
    }

    // ============================================================
    // MusicBrainz 封面搜索
    // ============================================================

    /// <summary>MusicBrainz 搜索封面（release 搜索 → Cover Art Archive）</summary>
    private async Task<List<SearchResult>> SearchMusicBrainzCoverAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var mbQuery = EscapeMusicBrainzQuery(query);
            var url = string.Format(MbReleaseSearchUrl,
                Uri.EscapeDataString(mbQuery), limit, offset);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return results;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var releases = obj["releases"];
            if (releases == null) return results;

            foreach (var release in releases.AsArray())
            {
                var releaseId = release.Get("id")?.ToString();
                if (string.IsNullOrEmpty(releaseId)) continue;

                var title = release.Get("title")?.ToString() ?? "";
                var artist = GetMusicBrainzArtistCredit(release.Get("artist-credit"));
                var date = release.Get("date")?.ToString() ?? "";
                var year = "";
                if (!string.IsNullOrEmpty(date) && date.Length >= 4)
                    year = date.Substring(0, 4);

                var coverUrl = string.Format(CoverArtArchiveUrl, releaseId);

                results.Add(new SearchResult
                {
                    SourceName = "MusicBrainz",
                    SourceUrl = $"https://musicbrainz.org/release/{releaseId}",
                    Title = title,
                    Artist = artist,
                    Album = title,
                    Year = year,
                    CoverUrl = coverUrl,
                });
            }
        }
        catch { /* ignore */ }
        return results;
    }

    /// <summary>MusicBrainz 标签搜索（recording 搜索 → 标签元数据 + 封面）</summary>
    public async Task<List<SearchResult>> SearchMusicBrainzTagsAsync(
        string query, int limit, CancellationToken ct)
    {
        return await SearchMusicBrainzTagsAsync(query, limit, 0, ct);
    }

    public async Task<List<SearchResult>> SearchMusicBrainzTagsAsync(
        string query, int limit, int offset, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var http = GetHttpClientForSource(SourceMusicBrainz);
            var mbQuery = EscapeMusicBrainzQuery(query);
            var url = string.Format(MbRecordingSearchUrl,
                Uri.EscapeDataString(mbQuery), limit, offset);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return results;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var recordings = obj["recordings"];
            if (recordings == null) return results;

            foreach (var recording in recordings.AsArray())
            {
                var recTitle = recording.Get("title")?.ToString() ?? "";
                var artist = GetMusicBrainzArtistCredit(recording.Get("artist-credit"));

                var releases = recording.Get("releases");
                if (releases == null || releases.AsArray().Count == 0) continue;

                // 取第一个 release 的信息
                var firstRelease = releases.AsArray()[0];
                var releaseId = firstRelease.Get("id")?.ToString() ?? "";
                var album = firstRelease.Get("title")?.ToString() ?? "";
                var date = firstRelease.Get("date")?.ToString() ?? "";
                var year = "";
                if (!string.IsNullOrEmpty(date) && date.Length >= 4)
                    year = date.Substring(0, 4);

                // 从 media 获取 track/disc 信息
                int? track = null;
                int? disc = null;
                int? discCount = null;
                var media = firstRelease.Get("media");
                if (media != null)
                {
                    discCount = media.AsArray().Count;
                    foreach (var m in media.AsArray())
                    {
                        var tracks = m.Get("tracks");
                        if (tracks == null) continue;
                        foreach (var t in tracks.AsArray())
                        {
                            var recId = t.Get("recording")?.AsObject()["id"]?.ToString();
                            if (recId == recording.Get("id")?.ToString())
                            {
                                track = t.Get("position")?.GetValue<int>();
                                disc = m.Get("position")?.GetValue<int>();
                                break;
                            }
                        }
                    }
                }

                var coverUrl = string.IsNullOrEmpty(releaseId)
                    ? null
                    : string.Format(CoverArtArchiveUrl, releaseId);

                var result = new SearchResult
                {
                    SourceName = "MusicBrainz",
                    SourceUrl = $"https://musicbrainz.org/recording/{recording.Get("id")}",
                    Title = recTitle,
                    Artist = artist,
                    Album = album,
                    Year = year,
                    CoverUrl = coverUrl,
                };
                if (track.HasValue)
                    result.ExtraFields["track"] = track.Value.ToString();
                if (disc.HasValue)
                    result.ExtraFields["disc"] = disc.Value.ToString();
                if (discCount.HasValue)
                    result.ExtraFields["discCount"] = discCount.Value.ToString();

                results.Add(result);
            }
        }
        catch { /* ignore */ }
        return results;
    }

    /// <summary>从网易云 song 对象提取年份（e 字段为 epoch 秒，publishTime 为毫秒）</summary>
    private static string? GetNeteaseYear(JsonNode? song)
    {
        // 优先取 publishTime（毫秒时间戳）
        var pt = song.Get("publishTime");
        if (pt.NotNull() && pt!.GetValue<long>() > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(pt!.GetValue<long>()).Year.ToString();

        // 回退取 e（秒时间戳）
        var e = song.Get("e");
        if (e.NotNull() && e!.GetValue<long>() > 0)
            return DateTimeOffset.FromUnixTimeSeconds(e!.GetValue<long>()).Year.ToString();

        return null;
    }

    /// <summary>提取 MusicBrainz artist-credit 数组中的艺术家名</summary>
    private static string GetMusicBrainzArtistCredit(JsonNode? artistCredit)
    {
        if (artistCredit == null || artistCredit!.AsArray().Count == 0) return "";
        var names = artistCredit!.AsArray().Select(ac => ac.Get("name")?.ToString() ?? "").Where(n => n != "");
        return string.Join(", ", names);
    }

    /// <summary>转义 MusicBrainz Lucene 查询中的特殊字符</summary>
    private static string EscapeMusicBrainzQuery(string query)
    {
        // 转义 Lucene 特殊字符
        var special = @"\+-&|!(){}[]^~*?:/";
        var sb = new System.Text.StringBuilder(query.Length * 2);
        foreach (var c in query)
        {
            if (special.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ============================================================
    // Discogs 封面搜索 + 标签搜索
    // ============================================================

    /// <summary>Discogs 搜索封面（搜索 → 获取 release 详情中的图片）</summary>
    /// <remarks>
    /// Discogs 搜索结果的 cover_image 字段在未认证请求中为空，
    /// 需要通过 release 详情接口获取 images 数组中的图片 URL。
    /// 为避免触发 Discogs API 速率限制（60 请求/分钟），仅对前 3 个结果获取详情。
    /// </remarks>
    private async Task<List<SearchResult>> SearchDiscogsCoverAsync(
        string query, int limit, int offset, HttpClient http, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var url = string.Format(DiscogsSearchUrl,
                Uri.EscapeDataString(query), limit, offset / Math.Max(1, limit) + 1);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return results;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var items = obj["results"];
            if (items == null) return results;

            // 先收集所有搜索结果的基本信息
            var candidates = new List<(string id, string artist, string album, string year, string genre)>();
            foreach (var item in items.AsArray())
            {
                var id = item.Get("id")?.ToString();
                if (string.IsNullOrEmpty(id)) continue;

                var title = item.Get("title")?.ToString() ?? "";
                var year = item.Get("year")?.ToString() ?? "";
                var genre = item.Get("genre")?.AsArray().FirstOrDefault()?.ToString() ?? "";

                var artist = "";
                var album = title;
                var dashIdx = title.IndexOf(" - ");
                if (dashIdx > 0)
                {
                    artist = title.Substring(0, dashIdx);
                    album = title.Substring(dashIdx + 3);
                }

                candidates.Add((id, artist, album, year, genre));
            }

            // 仅对前 3 个结果获取详情（避免速率限制）
            var detailLimit = Math.Min(3, candidates.Count);
            for (var i = 0; i < detailLimit; i++)
            {
                if (ct.IsCancellationRequested) break;
                var c = candidates[i];

                var coverUrl = await FetchDiscogsCoverUrlAsync(c.id, http, ct);
                if (string.IsNullOrEmpty(coverUrl)) continue;

                results.Add(new SearchResult
                {
                    SourceName = "Discogs",
                    SourceUrl = $"https://www.discogs.com/release/{c.id}",
                    Title = c.album,
                    Artist = c.artist,
                    Album = c.album,
                    Year = c.year,
                    Genre = c.genre,
                    CoverUrl = coverUrl,
                });
            }
        }
        catch { /* ignore */ }
        return results;
    }

    /// <summary>从 Discogs release 详情中提取封面图片 URL</summary>
    private async Task<string?> FetchDiscogsCoverUrlAsync(string releaseId, HttpClient http, CancellationToken ct)
    {
        try
        {
            var url = string.Format(DiscogsReleaseDetailUrl, releaseId);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;

            // 从 images 数组中取第一张 primary 类型图片
            var images = obj["images"];
            if (images == null || images!.AsArray().Count == 0) return null;

            var primary = images!.AsArray().FirstOrDefault(img => img.Get("type")?.ToString() == "primary")
                ?? images!.AsArray()[0];
            return primary.Get("uri")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Discogs 标签搜索（搜索 + 获取 release 详情中的 tracklist + 封面）</summary>
    public async Task<List<SearchResult>> SearchDiscogsTagsAsync(
        string query, int limit, CancellationToken ct)
    {
        return await SearchDiscogsTagsAsync(query, limit, 0, ct);
    }

    public async Task<List<SearchResult>> SearchDiscogsTagsAsync(
        string query, int limit, int offset, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            var http = GetHttpClientForSource(SourceDiscogs);
            var url = string.Format(DiscogsSearchUrl,
                Uri.EscapeDataString(query), limit, offset / Math.Max(1, limit) + 1);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return results;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;
            var items = obj["results"];
            if (items == null) return results;

            // 取前几个 release 获取详情（带 tracklist + 封面）
            var count = 0;
            foreach (var item in items.AsArray())
            {
                if (count >= limit) break;

                var id = item.Get("id")?.ToString();
                if (string.IsNullOrEmpty(id)) continue;

                var title = item.Get("title")?.ToString() ?? "";
                var year = item.Get("year")?.ToString() ?? "";
                var genre = item.Get("genre")?.AsArray().FirstOrDefault()?.ToString() ?? "";

                // Discogs title 格式: "Artist - Album"
                var artist = "";
                var album = title;
                var dashIdx = title.IndexOf(" - ");
                if (dashIdx > 0)
                {
                    artist = title.Substring(0, dashIdx);
                    album = title.Substring(dashIdx + 3);
                }

                // 获取 release 详情以提取 tracklist 和封面
                var detail = await FetchDiscogsReleaseDetailAsync(id, http, ct);

                var result = new SearchResult
                {
                    SourceName = "Discogs",
                    SourceUrl = $"https://www.discogs.com/release/{id}",
                    Title = album,
                    Artist = artist,
                    Album = album,
                    Year = year,
                    Genre = genre,
                    CoverUrl = detail?.CoverUrl,
                };

                if (detail != null)
                {
                    if (!string.IsNullOrEmpty(detail.Country))
                        result.ExtraFields["country"] = detail.Country;
                    if (detail.TrackCount > 0)
                        result.ExtraFields["trackCount"] = detail.TrackCount.ToString();
                }

                results.Add(result);
                count++;
            }
        }
        catch { /* ignore */ }
        return results;
    }

    private class DiscogsReleaseDetail
    {
        public string? Country { get; set; }
        public int TrackCount { get; set; }
        public string? CoverUrl { get; set; }
    }

    private async Task<DiscogsReleaseDetail?> FetchDiscogsReleaseDetailAsync(
        string releaseId, HttpClient http, CancellationToken ct)
    {
        try
        {
            var url = string.Format(DiscogsReleaseDetailUrl, releaseId);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "MusicTagRecover/1.0 ( https://github.com/example/musictag-clone )");
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var obj = JsonObject.Parse(json)!;

            var country = obj["country"]?.ToString();
            var tracklist = obj["tracklist"];
            var trackCount = tracklist?.AsArray().Count ?? 0;

            // 从 images 数组中取第一张 primary 类型图片
            string? coverUrl = null;
            var images = obj["images"];
            if (images != null && images!.AsArray().Count > 0)
            {
                var primary = images!.AsArray().FirstOrDefault(img => img.Get("type")?.ToString() == "primary")
                    ?? images!.AsArray()[0];
                coverUrl = primary.Get("uri")?.ToString();
            }

            return new DiscogsReleaseDetail
            {
                Country = country,
                TrackCount = trackCount,
                CoverUrl = coverUrl,
            };
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>将显示名映射回源键名</summary>
    private static string GetSourceKeyFromDisplayName(string? displayName) => displayName switch
    {
        "网易云音乐" => SourceNetease,
        "QQ音乐" => SourceQQ,
        "iTunes" => SourceITunes,
        "酷我音乐" => SourceKuwo,
        "Last.fm" => SourceLastfm,
        "MusicBrainz" => SourceMusicBrainz,
        "Discogs" => SourceDiscogs,
        _ => SourceDefault,
    };

    private static double CalculateMatchScore(MusicFile file, SearchResult result)
    {
        var score = 0.0;
        var maxScore = 0.0;

        if (!string.IsNullOrEmpty(file.Title))
        {
            maxScore += 40;
            if (!string.IsNullOrEmpty(result.Title) &&
                result.Title.IndexOf(file.Title, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 40;
        }

        if (!string.IsNullOrEmpty(file.Artist))
        {
            maxScore += 35;
            if (!string.IsNullOrEmpty(result.Artist) &&
                result.Artist.IndexOf(file.Artist, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 35;
        }

        if (!string.IsNullOrEmpty(file.Album))
        {
            maxScore += 25;
            if (!string.IsNullOrEmpty(result.Album) &&
                result.Album.IndexOf(file.Album, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 25;
        }

        return maxScore > 0 ? score / maxScore : 0;
    }

    private static string GetMimeTypeFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return GetMimeTypeFromExtension(ext);
        }
        catch
        {
            return "image/jpeg";
        }
    }

    private static string GetMimeTypeFromExtension(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    private static ImageCodecInfo? GetEncoder(string mimeType)
        => ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == mimeType);
}
