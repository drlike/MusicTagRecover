using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.Services;

/// <summary>
/// 综合网络搜索服务 — 协调歌词、封面、标签的在线搜索
/// </summary>
public class WebSearchService
{
    private readonly ILyricService _lyricService;
    private readonly ICoverService _coverService;
    private readonly ISettingsService _settings;
    private readonly ILoggerService? _logger;

    public WebSearchService(ILyricService lyricService, ICoverService coverService,
        ISettingsService settings, ILoggerService? logger = null)
    {
        _lyricService = lyricService;
        _coverService = coverService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 自动匹配标签 — 从多个在线源搜索匹配的标签信息
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> AutoMatchTagsAsync(
        MusicFile file, CancellationToken ct = default)
    {
        var condition = SearchCondition.FromSettings(_settings);
        condition.WebSearchItemsLimit = _settings.WebSearchItemsLimit;

        _logger?.Debug("[标签源][单文件自动匹配] 开始: 文件={0}, Artist={1}, Title={2}, Album={3}, " +
            "UseTitle={4}, UseArtist={5}, UseAlbum={6}, Limit={7}",
            file.FilePath, file.Artist, file.Title, file.Album,
            condition.UseTitle, condition.UseArtist, condition.UseAlbum,
            condition.WebSearchItemsLimit);

        // 组合标签、图片和歌词分别使用各自的来源列表。列表顺序保持为来源优先级，
        // OrderByDescending 的稳定排序会在匹配分相同时保留该优先级。
        var allResults = new List<SearchResult>();
        foreach (var source in LoadEnabledSources(TagSourceCategory.CombinationTags,
            _settings.CombTagsInfo_SourceItemList))
        {
            var found = await SearchCoversFromSourceAsync(file, TagSourceCategory.CombinationTags,
                source, condition, ct);
            allResults.AddRange(found);
        }

        foreach (var source in LoadEnabledSources(TagSourceCategory.Picture,
            _settings.PictureInfo_SourceItemList))
        {
            var found = await SearchCoversFromSourceAsync(file, TagSourceCategory.Picture,
                source, condition, ct);
            allResults.AddRange(found);
        }

        foreach (var source in LoadEnabledSources(TagSourceCategory.Lyrics,
            _settings.LyricInfo_SourceItemList))
        {
            var started = DateTime.UtcNow;
            _logger?.Debug("[标签源][单文件自动匹配] 请求开始: 类别=Lyrics, 序号={0}, 源={1}, " +
                "limit={2}", source.Sequence + 1, source.Key, source.WebSearchItemsLimit);
            try
            {
                var found = await _lyricService.SearchLyricsFromSourceAsync(file, source.Key,
                    CopyCondition(condition, source.WebSearchItemsLimit),
                    new LyricInfo.DownloadConfig(), ct);
                allResults.AddRange(found);
                LogSourceResult("Lyrics", source, found.Count, found.OrderByDescending(r => r.MatchScore).FirstOrDefault(), started);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Error(ex, "[标签源][单文件自动匹配] 请求失败: 类别=Lyrics, 源={0}", source.Key);
                throw;
            }
        }

        var results = allResults
            .OrderByDescending(r => r.MatchScore)
            .GroupBy(r => $"{r.Artist}|{r.Title}|{r.Album}")
            .Select(g => g.First())
            .Take(_settings.WebSearchItemsLimit)
            .ToList();
        _logger?.Debug("[标签源][单文件自动匹配] 完成: 原始结果={0}, 去重后={1}, 首选={2}",
            allResults.Count, results.Count, DescribeResult(results.FirstOrDefault()));
        return results;
    }

    private List<TagSourceItem> LoadEnabledSources(TagSourceCategory category, string? json)
    {
        var all = TagSourceCatalog.Load(json, category, _settings.WebSearchItemsLimit);
        var enabled = all.Where(s => s.Enabled).ToList();
        _logger?.Debug("[标签源][单文件自动匹配] 配置读取: 类别={0}, 全部={1}",
            category, TagSourceCatalog.Describe(all));
        _logger?.Debug("[标签源][单文件自动匹配] 实际启用顺序: 类别={0}, {1}",
            category, enabled.Count == 0 ? "无" : TagSourceCatalog.Describe(enabled));
        return enabled;
    }

    private async Task<IReadOnlyList<SearchResult>> SearchCoversFromSourceAsync(
        MusicFile file, TagSourceCategory category, TagSourceItem source,
        SearchCondition condition, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        _logger?.Debug("[标签源][单文件自动匹配] 请求开始: 类别={0}, 序号={1}, 源={2}, limit={3}",
            category, source.Sequence + 1, source.Key, source.WebSearchItemsLimit);
        try
        {
            var found = await _coverService.SearchCoversFromSourceAsync(file, source.Key,
                CopyCondition(condition, source.WebSearchItemsLimit), ct);
            LogSourceResult(category.ToString(), source, found.Count,
                found.OrderByDescending(r => r.MatchScore).FirstOrDefault(), started);
            return found;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger?.Error(ex, "[标签源][单文件自动匹配] 请求失败: 类别={0}, 源={1}",
                category, source.Key);
            throw;
        }
    }

    private void LogSourceResult(string category, TagSourceItem source, int count,
        SearchResult? best, DateTime started)
    {
        _logger?.Debug("[标签源][单文件自动匹配] 请求完成: 类别={0}, 序号={1}, 源={2}, " +
            "结果数={3}, 耗时Ms={4}, 最佳={5}", category, source.Sequence + 1, source.Key,
            count, (DateTime.UtcNow - started).TotalMilliseconds.ToString("F0"),
            DescribeResult(best));
    }

    private static string DescribeResult(SearchResult? result) => result == null
        ? "无"
        : string.Format("source={0}, artist={1}, title={2}, album={3}, score={4:F3}, cover={5}",
            result.SourceName ?? "", result.Artist ?? "", result.Title ?? "", result.Album ?? "",
            result.MatchScore, !string.IsNullOrEmpty(result.CoverUrl));

    private static SearchCondition CopyCondition(SearchCondition source, int sourceLimit) => new()
    {
        UseTitle = source.UseTitle,
        UseArtist = source.UseArtist,
        UseAlbum = source.UseAlbum,
        FieldOrder = source.FieldOrder.ToList(),
        ItunesCountry = source.ItunesCountry,
        WebSearchItemsLimit = sourceLimit > 0 && sourceLimit <= 99
            ? sourceLimit : source.WebSearchItemsLimit
    };

    /// <summary>
    /// 批量自动匹配标签 — 多线程处理
    /// </summary>
    public async Task<Dictionary<string, List<SearchResult>>> BatchAutoMatchAsync(
        IEnumerable<MusicFile> files, IProgress<int>? progress = null)
    {
        var fileList = files.ToList();
        var results = new Dictionary<string, List<SearchResult>>();
        var threadCount = Math.Max(1, Math.Min(AutoMatchOptions.MaxThreadCount,
            _settings.AutoMatchTagsWebSearchThreadCount));
        _logger?.Debug("[标签源][批量自动匹配-搜索预览] 开始: 文件数={0}, 线程数={1}",
            fileList.Count, threadCount);
        var semaphore = new SemaphoreSlim(threadCount);
        var completed = 0;

        var tasks = fileList.Select(async file =>
        {
            await semaphore.WaitAsync();
            try
            {
                var matches = await AutoMatchTagsAsync(file);
                lock (results)
                {
                    results[file.FilePath] = matches.ToList();
                }
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(done);
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        _logger?.Debug("[标签源][批量自动匹配-搜索预览] 完成: 文件数={0}, 结果文件数={1}",
            fileList.Count, results.Count);
        return results;
    }
}
