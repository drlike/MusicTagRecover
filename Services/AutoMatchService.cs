using System.Text;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.Services;

public sealed class AutoMatchFileResult
{
    public string FilePath { get; set; } = string.Empty;
    public bool Matched { get; set; }
    public bool Written { get; set; }
    public bool Skipped { get; set; }
    public string? Error { get; set; }
}

public sealed class AutoMatchBatchResult
{
    public List<AutoMatchFileResult> Files { get; } = new();
    public int MatchedCount => Files.Count(f => f.Matched);
    public int WrittenCount => Files.Count(f => f.Written);
    public int SkippedCount => Files.Count(f => f.Skipped);
    public int ErrorCount => Files.Count(f => !string.IsNullOrEmpty(f.Error));
}

/// <summary>执行批量自动匹配标签的搜索、合并和落盘规则。</summary>
public sealed class AutoMatchService
{
    private readonly ISettingsService _settings;
    private readonly ITagService _tagService;
    private readonly ILyricService _lyricService;
    private readonly ICoverService _coverService;
    private readonly ILoggerService _logger;

    public AutoMatchService(ISettingsService settings, ITagService tagService,
        ILyricService lyricService, ICoverService coverService, ILoggerService logger)
    {
        _settings = settings;
        _tagService = tagService;
        _lyricService = lyricService;
        _coverService = coverService;
        _logger = logger;
    }

    public async Task<AutoMatchBatchResult> ExecuteAsync(IEnumerable<MusicFile> files,
        AutoMatchOptions options, IProgress<int>? progress = null,
        IProgress<string>? currentFile = null, bool overwriteReadOnly = false,
        CancellationToken ct = default)
    {
        var list = files.ToList();
        var result = new AutoMatchBatchResult();
        var threadCount = Math.Max(1, Math.Min(AutoMatchOptions.MaxThreadCount, options.ThreadCount));
        _logger.Debug("[标签源][批量自动匹配] 开始: 文件数={0}, 线程数={1}, 覆盖只读={2}, " +
            "跳过伴奏歌词={3}, 启用选项={4}", list.Count, threadCount, overwriteReadOnly,
            options.DontDownloadLyricWithInstrumentInTitle, DescribeOptions(options));
        using var gate = new SemaphoreSlim(threadCount);
        var completed = 0;
        var tasks = list.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                currentFile?.Report(file.FileName);
                _logger.Debug("[标签源][批量自动匹配] 文件开始: 文件={0}, 线程={1}",
                    file.FilePath, Environment.CurrentManagedThreadId);
                var one = await ProcessFileAsync(file, options, overwriteReadOnly, ct).ConfigureAwait(false);
                lock (result.Files) result.Files.Add(one);
                _logger.Debug("[标签源][批量自动匹配] 文件完成: 文件={0}, matched={1}, written={2}, " +
                    "skipped={3}, error={4}", file.FilePath, one.Matched, one.Written,
                    one.Skipped, one.Error ?? "无");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Error(ex, "[标签源][批量自动匹配] 文件异常: {0}", file.FilePath);
                lock (result.Files) result.Files.Add(new AutoMatchFileResult
                {
                    FilePath = file.FilePath, Error = ex.Message
                });
            }
            finally
            {
                Interlocked.Increment(ref completed);
                progress?.Report(completed);
                gate.Release();
            }
        }).ToList();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _logger.Debug("[标签源][批量自动匹配] 批次完成: 完成数={0}/{1}, matched={2}, written={3}, " +
            "skipped={4}, errors={5}", result.Files.Count, list.Count, result.MatchedCount,
            result.WrittenCount, result.SkippedCount, result.ErrorCount);
        return result;
    }

    private async Task<AutoMatchFileResult> ProcessFileAsync(MusicFile file,
        AutoMatchOptions options, bool overwriteReadOnly, CancellationToken ct)
    {
        var attributes = File.GetAttributes(file.FilePath);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        _logger.Debug("[标签源][批量自动匹配] 文件状态: 文件={0}, 只读={1}, 有标签字段={2}",
            file.FilePath, readOnly, options.HasTagFields);
        if (readOnly && options.HasTagFields && !overwriteReadOnly)
        {
            _logger.Debug("[标签源][批量自动匹配] 跳过只读文件: 文件={0}", file.FilePath);
            return new AutoMatchFileResult { FilePath = file.FilePath, Skipped = true };
        }
        if (readOnly && options.HasTagFields)
            File.SetAttributes(file.FilePath, attributes & ~FileAttributes.ReadOnly);
        try
        {
            var current = CloneFile(file);
            var tags = await _tagService.ReadTagsAsync(file.FilePath).ConfigureAwait(false);
            ApplyReadTags(current, tags);
            _logger.Debug("[标签源][批量自动匹配] 当前标签: 文件={0}, Artist={1}, Title={2}, Album={3}, " +
                "Year={4}, Track={5}, Lyrics={6}, Cover={7}", file.FilePath, current.Artist,
                current.Title, current.Album, current.Year?.ToString() ?? "", current.Track?.ToString() ?? "",
                current.HasLyrics, current.HasCoverArt);
            var needsMetadata = options.Fields.Any(p => p.Value.Enabled &&
                p.Key != AutoMatchOptions.Lyrics && p.Key != AutoMatchOptions.Cover);
            var needsLyrics = options.Get(AutoMatchOptions.Lyrics).Enabled;
            var needsCover = options.Get(AutoMatchOptions.Cover).Enabled;
            var needsCombinationTags = needsMetadata || needsCover;
            _logger.Debug("[标签源][批量自动匹配] 搜索需求: 文件={0}, 组合标签={1}, 图片={2}, 歌词={3}",
                file.FilePath, needsCombinationTags, needsCover, needsLyrics);
            if (!needsMetadata && !needsLyrics && !needsCover)
            {
                _logger.Debug("[标签源][批量自动匹配] 无启用的匹配项，跳过: 文件={0}", file.FilePath);
                return new AutoMatchFileResult { FilePath = file.FilePath, Skipped = true };
            }

            var condition = SearchCondition.FromSettings(_settings);
            condition.ItunesCountry = _settings.ItunesSearchParamsCountry;
            condition.WebSearchItemsLimit = Math.Max(1, _settings.WebSearchItemsLimit);
            SearchResult? metadata = null;
            var metadataCandidates = new List<SearchResult>();
            if (needsCombinationTags)
            {
                foreach (var source in LoadSources(_settings.CombTagsInfo_SourceItemList,
                    TagSourceCategory.CombinationTags))
                {
                    var sourceCondition = CopyCondition(condition, source.Limit);
                    var found = await SearchCoverSourceAsync(current, TagSourceCategory.CombinationTags,
                        source, sourceCondition, ct).ConfigureAwait(false);
                    metadataCandidates.AddRange(found);
                }
                if (needsMetadata)
                    metadata = metadataCandidates.OrderByDescending(c => c.MatchScore)
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Title) ||
                                             !string.IsNullOrWhiteSpace(c.Artist) ||
                                             !string.IsNullOrWhiteSpace(c.Album) ||
                                             !string.IsNullOrWhiteSpace(c.CoverUrl));
                _logger.Debug("[标签源][批量自动匹配] 组合标签选中: 文件={0}, 候选数={1}, 结果={2}",
                    file.FilePath, metadataCandidates.Count, DescribeResult(metadata));
            }

            SearchResult? coverResult = null;
            var pictureCandidates = new List<SearchResult>();
            if (needsCover)
            {
                foreach (var source in LoadSources(_settings.PictureInfo_SourceItemList,
                    TagSourceCategory.Picture))
                {
                    var sourceCondition = CopyCondition(condition, source.Limit);
                    var found = await SearchCoverSourceAsync(current, TagSourceCategory.Picture,
                        source, sourceCondition, ct).ConfigureAwait(false);
                    pictureCandidates.AddRange(found);
                }
                coverResult = pictureCandidates.OrderByDescending(c => c.MatchScore)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.CoverUrl));
                _logger.Debug("[标签源][批量自动匹配] 图片源选中: 文件={0}, 候选数={1}, 结果={2}",
                    file.FilePath, pictureCandidates.Count, DescribeResult(coverResult));
            }

        LyricInfo? lyric = null;
        var instrumental = AutoMatchOptions.IsInstrumentalTitle(metadata?.Title ?? current.Title);
        if (needsLyrics && options.DontDownloadLyricWithInstrumentInTitle && instrumental)
            _logger.Debug("[标签源][批量自动匹配] 跳过歌词搜索: 文件={0}, 原因=伴奏标题", file.FilePath);
        if (needsLyrics && !(options.DontDownloadLyricWithInstrumentInTitle && instrumental))
        {
            var lyricResults = new List<SearchResult>();
            foreach (var source in LoadSources(_settings.LyricInfo_SourceItemList,
                TagSourceCategory.Lyrics))
            {
                var sourceCondition = CopyCondition(condition, source.Limit);
                var found = await SearchLyricSourceAsync(current, source, sourceCondition, ct)
                    .ConfigureAwait(false);
                lyricResults.AddRange(found);
            }
            var lyricResult = lyricResults.OrderByDescending(c => c.MatchScore)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.SourceUrl) || c.SourceName == "酷狗音乐");
            _logger.Debug("[标签源][批量自动匹配] 歌词源选中: 文件={0}, 候选数={1}, 结果={2}",
                file.FilePath, lyricResults.Count, DescribeResult(lyricResult));
            if (lyricResult != null)
                lyric = await DownloadLyricWithLoggingAsync(lyricResult, file.FilePath, ct)
                    .ConfigureAwait(false);
        }

        var writeTags = new TagData();
        ApplyText(writeTags, current, metadata, options);
        var lyricText = GetLyricText(lyric);
        if (!string.IsNullOrEmpty(lyricText) && options.Get(AutoMatchOptions.Lyrics).WriteMode != AutoMatchWriteMode.SaveToFile)
            writeTags.Lyrics = ShouldWrite(options.Get(AutoMatchOptions.Lyrics).Overwrite, current.Lyrics) ? lyricText : null;
        ApplyWrittenText(current, writeTags);

        CoverArt? cover = null;
        var coverOption = options.Get(AutoMatchOptions.Cover);
        SearchResult? coverSearchResult = null;
        if (coverOption.Enabled)
        {
            var orderedCoverCandidates = new List<SearchResult>();
            orderedCoverCandidates.AddRange(pictureCandidates
                .Where(c => !string.IsNullOrWhiteSpace(c.CoverUrl))
                .OrderByDescending(c => c.MatchScore));
            orderedCoverCandidates.AddRange(metadataCandidates
                .Where(c => !string.IsNullOrWhiteSpace(c.CoverUrl))
                .OrderByDescending(c => c.MatchScore));

            var seenCoverUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coverCandidates = orderedCoverCandidates
                .Where(c => seenCoverUrls.Add(c.CoverUrl!))
                .ToList();
            _logger.Debug("[标签源][批量自动匹配] 封面候选队列: 文件={0}, 数量={1}, 顺序={2}",
                file.FilePath, coverCandidates.Count, DescribeCoverCandidates(coverCandidates));

            foreach (var candidate in coverCandidates)
            {
                _logger.Debug("[标签源][批量自动匹配] 开始下载封面: 文件={0}, 结果={1}, 覆盖={2}",
                    file.FilePath, DescribeResult(candidate), coverOption.Overwrite);
                cover = await DownloadCoverWithLoggingAsync(candidate, file.FilePath,
                    coverOption, ct).ConfigureAwait(false);
                _logger.Debug("[标签源][批量自动匹配] 封面下载完成: 文件={0}, 源={1}, 成功={2}, 字节数={3}",
                    file.FilePath, candidate.SourceName, cover != null, cover?.ImageData?.Length ?? 0);
                if (cover != null)
                {
                    coverSearchResult = candidate;
                    break;
                }

                _logger.Debug("[标签源][批量自动匹配] 封面候选失败，尝试下一个: 文件={0}, 源={1}",
                    file.FilePath, candidate.SourceName);
            }

            if (coverSearchResult == null)
                _logger.Debug("[标签源][批量自动匹配] 所有封面候选均下载失败: 文件={0}", file.FilePath);
            if (cover != null && coverOption.WriteMode != AutoMatchWriteMode.SaveToFile &&
                (coverOption.Overwrite || !current.HasCoverArt))
                writeTags.AllPictures = new List<CoverArt> { cover };
        }

        // TagData.HasAnyValue covers scalar fields and lyrics, but cover-only
        // writes are carried in AllPictures and must still be persisted.
        var changed = writeTags.HasAnyValue ||
            (writeTags.AllPictures != null && writeTags.AllPictures.Count > 0);
        _logger.Debug("[标签源][批量自动匹配] 标签写入准备: 文件={0}, changed={1}, 字段={2}",
            file.FilePath, changed, DescribeTagData(writeTags));
        var tagWriteOk = !changed || await _tagService.WriteTagsAsync(file.FilePath, writeTags,
            _settings.SaveTagsKeepUpdateTime).ConfigureAwait(false);
        _logger.Debug("[标签源][批量自动匹配] 标签写入完成: 文件={0}, 执行={1}, 成功={2}",
            file.FilePath, changed, tagWriteOk);
        var written = changed && tagWriteOk;
        if (cover != null && coverOption.Enabled &&
            (coverOption.WriteMode == AutoMatchWriteMode.SaveToFile || coverOption.WriteMode == AutoMatchWriteMode.SaveToTagAndFile))
        {
            var coverFileWritten = await SaveCoverFileAsync(current, cover, coverOption.Overwrite, ct)
                .ConfigureAwait(false);
            _logger.Debug("[标签源][批量自动匹配] 封面文件写入完成: 文件={0}, 成功={1}",
                file.FilePath, coverFileWritten);
            written |= coverFileWritten;
        }
        var lyricOption = options.Get(AutoMatchOptions.Lyrics);
        var saveExternalLyric = !string.IsNullOrEmpty(lyricText) &&
            lyricOption.WriteMode != AutoMatchWriteMode.SaveToTag;
        if (saveExternalLyric)
        {
            var lyricFileWritten = await SaveLyricFileAsync(current, lyric!, lyricOption.Overwrite, ct)
                .ConfigureAwait(false);
            _logger.Debug("[标签源][批量自动匹配] 歌词文件写入完成: 文件={0}, 成功={1}",
                file.FilePath, lyricFileWritten);
            written |= lyricFileWritten;
        }

        var finalResult = new AutoMatchFileResult
        {
            FilePath = file.FilePath,
            Matched = metadata != null || coverResult != null || lyric != null,
            Written = written,
            Skipped = !written,
            Error = changed && !tagWriteOk ? "标签写入失败" : null
        };
        _logger.Debug("[标签源][批量自动匹配] 文件结果: 文件={0}, matched={1}, written={2}, skipped={3}, error={4}",
            file.FilePath, finalResult.Matched, finalResult.Written, finalResult.Skipped,
            finalResult.Error ?? "无");
        return finalResult;
        }
        finally
        {
            if (readOnly && options.HasTagFields && File.Exists(file.FilePath))
                File.SetAttributes(file.FilePath, attributes);
        }
    }

    private static void ApplyText(TagData target, MusicFile file, SearchResult? result, AutoMatchOptions options)
    {
        if (result == null) return;
        var map = new Dictionary<string, Action<string>>
        {
            [AutoMatchOptions.Title] = v => target.Title = v,
            [AutoMatchOptions.Artist] = v => target.Artist = v,
            [AutoMatchOptions.Album] = v => target.Album = v,
            [AutoMatchOptions.Genre] = v => target.Genre = v,
            [AutoMatchOptions.Comment] = v => target.Comment = v
        };
        foreach (var pair in map)
        {
            var option = options.Get(pair.Key);
            var value = pair.Key == AutoMatchOptions.Title ? result.Title :
                pair.Key == AutoMatchOptions.Artist ? result.Artist :
                pair.Key == AutoMatchOptions.Album ? result.Album :
                pair.Key == AutoMatchOptions.Genre ? result.Genre : result.ExtraFields.TryGetValue("comment", out var c) ? c : null;
            if (option.Enabled && option.WriteMode != AutoMatchWriteMode.SaveToFile &&
                !string.IsNullOrWhiteSpace(value) && ShouldWrite(option.Overwrite, ExistingText(file, pair.Key))) pair.Value(value!);
        }
        var year = result.Year;
        var yearOption = options.Get(AutoMatchOptions.Year);
        if (yearOption.Enabled && yearOption.WriteMode != AutoMatchWriteMode.SaveToFile &&
            uint.TryParse(year, out var y) && ShouldWrite(yearOption.Overwrite, file.Year)) target.Year = y;
        var trackOption = options.Get(AutoMatchOptions.Track);
        if (trackOption.Enabled && trackOption.WriteMode != AutoMatchWriteMode.SaveToFile)
        {
            if (TryExtra(result, "track", out var track) && ShouldWrite(trackOption.Overwrite, file.Track)) target.Track = track;
            if (TryExtra(result, "trackCount", out var trackCount) && ShouldWrite(trackOption.Overwrite, file.TrackCount)) target.TrackCount = trackCount;
        }
        var discOption = options.Get(AutoMatchOptions.Disc);
        if (discOption.Enabled && discOption.WriteMode != AutoMatchWriteMode.SaveToFile)
        {
            if (TryExtra(result, "disc", out var disc) && ShouldWrite(discOption.Overwrite, file.Disc)) target.Disc = disc;
            if (TryExtra(result, "discCount", out var discCount) && ShouldWrite(discOption.Overwrite, file.DiscCount)) target.DiscCount = discCount;
        }
    }

    private static bool TryExtra(SearchResult result, string key, out uint value)
    {
        value = 0;
        return result.ExtraFields.TryGetValue(key, out var raw) && uint.TryParse(raw, out value);
    }

    private static string? ExistingText(MusicFile file, string key) => key switch
    {
        AutoMatchOptions.Title => file.Title,
        AutoMatchOptions.Artist => file.Artist,
        AutoMatchOptions.Album => file.Album,
        AutoMatchOptions.Genre => file.Genre,
        AutoMatchOptions.Comment => file.Comment,
        _ => null
    };

    private static bool ShouldWrite(bool overwrite, string? existing) => overwrite || string.IsNullOrWhiteSpace(existing);
    private static bool ShouldWrite(bool overwrite, uint? existing) => overwrite || !existing.HasValue || existing.Value == 0;

    private SearchCondition CopyCondition(SearchCondition source, int sourceLimit) => new()
    {
        UseTitle = source.UseTitle,
        UseArtist = source.UseArtist,
        UseAlbum = source.UseAlbum,
        FieldOrder = source.FieldOrder.ToList(),
        ItunesCountry = source.ItunesCountry,
        WebSearchItemsLimit = sourceLimit > 0 && sourceLimit <= 99
            ? sourceLimit : source.WebSearchItemsLimit
    };

    private async Task<IReadOnlyList<SearchResult>> SearchCoverSourceAsync(
        MusicFile file, TagSourceCategory category, SourceSetting source,
        SearchCondition condition, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        _logger.Debug("[标签源][批量自动匹配] 请求开始: 类别={0}, 序号={1}, 源={2}, limit={3}, " +
            "query={4}, UseTitle={5}, UseArtist={6}, UseAlbum={7}", category,
            source.Sequence + 1, source.Key, condition.WebSearchItemsLimit,
            condition.BuildSearchQuery(file), condition.UseTitle, condition.UseArtist, condition.UseAlbum);
        try
        {
            var found = await _coverService.SearchCoversFromSourceAsync(file, source.Key,
                condition, ct).ConfigureAwait(false);
            LogSourceResult(category, source, found.Count,
                found.OrderByDescending(r => r.MatchScore).FirstOrDefault(), started);
            return found;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.Error(ex, "[标签源][批量自动匹配] 请求失败: 类别={0}, 序号={1}, 源={2}",
                category, source.Sequence + 1, source.Key);
            throw;
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchLyricSourceAsync(
        MusicFile file, SourceSetting source, SearchCondition condition, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        _logger.Debug("[标签源][批量自动匹配] 请求开始: 类别={0}, 序号={1}, 源={2}, limit={3}, " +
            "query={4}, UseTitle={5}, UseArtist={6}, UseAlbum={7}", TagSourceCategory.Lyrics,
            source.Sequence + 1, source.Key, condition.WebSearchItemsLimit,
            condition.BuildSearchQuery(file), condition.UseTitle, condition.UseArtist, condition.UseAlbum);
        try
        {
            var found = await _lyricService.SearchLyricsFromSourceAsync(file, source.Key,
                condition, CreateLyricDownloadConfig(), ct).ConfigureAwait(false);
            LogSourceResult(TagSourceCategory.Lyrics, source, found.Count,
                found.OrderByDescending(r => r.MatchScore).FirstOrDefault(), started);
            return found;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.Error(ex, "[标签源][批量自动匹配] 请求失败: 类别={0}, 序号={1}, 源={2}",
                TagSourceCategory.Lyrics, source.Sequence + 1, source.Key);
            throw;
        }
    }

    private async Task<CoverArt?> DownloadCoverWithLoggingAsync(SearchResult result,
        string filePath, AutoMatchFieldOption option, CancellationToken ct)
    {
        try
        {
            var downloadResult = await _coverService.DownloadCoverAsync(result, new CoverArt.LimitsConfig
            {
                FormatLimits = _settings.PictureFormatLimits ?? "jpg,jpeg,png,bmp,gif",
                MaxResolution = _settings.PictureResolutionLimits,
                MaxSizeKB = _settings.PictureSizeLimitsKB,
                OverwriteExisting = option.Overwrite
            }, ct).ConfigureAwait(false);
            if (downloadResult is not { Success: true })
            {
                _logger.Debug("[标签源][批量自动匹配] 封面验证失败: 文件={0}, 源={1}, 原因={2}",
                    filePath, result.SourceName, downloadResult?.ValidationError);
            }
            return downloadResult?.Cover;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.Error(ex, "[标签源][批量自动匹配] 封面下载失败: 文件={0}, 源={1}",
                filePath, result.SourceName);
            return null;
        }
    }

    private async Task<LyricInfo?> DownloadLyricWithLoggingAsync(SearchResult result,
        string filePath, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        _logger.Debug("[标签源][批量自动匹配] 开始下载歌词: 文件={0}, 源={1}, 结果={2}",
            filePath, result.SourceName, DescribeResult(result));
        try
        {
            var lyric = await _lyricService.DownloadLyricAsync(result, CreateLyricDownloadConfig(), ct)
                .ConfigureAwait(false);
            _logger.Debug("[标签源][批量自动匹配] 歌词下载完成: 文件={0}, 源={1}, 成功={2}, " +
                "原文长度={3}, 翻译长度={4}, 耗时Ms={5}", filePath, result.SourceName,
                lyric != null, lyric?.OriginalLyric?.Length ?? 0, lyric?.TranslatedLyric?.Length ?? 0,
                (DateTime.UtcNow - started).TotalMilliseconds.ToString("F0"));
            return lyric;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.Error(ex, "[标签源][批量自动匹配] 歌词下载失败: 文件={0}, 源={1}",
                filePath, result.SourceName);
            throw;
        }
    }

    private void LogSourceResult(TagSourceCategory category, SourceSetting source, int count,
        SearchResult? best, DateTime started)
    {
        _logger.Debug("[标签源][批量自动匹配] 请求完成: 类别={0}, 序号={1}, 源={2}, 结果数={3}, " +
            "耗时Ms={4}, 最佳={5}", category, source.Sequence + 1, source.Key, count,
            (DateTime.UtcNow - started).TotalMilliseconds.ToString("F0"), DescribeResult(best));
    }

    private static string DescribeResult(SearchResult? result) => result == null
        ? "无"
        : string.Format("source={0}, artist={1}, title={2}, album={3}, score={4:F3}, cover={5}",
            result.SourceName ?? "", result.Artist ?? "", result.Title ?? "", result.Album ?? "",
            result.MatchScore, !string.IsNullOrEmpty(result.CoverUrl));

    private static string DescribeCoverCandidates(IEnumerable<SearchResult> candidates) =>
        string.Join(" -> ", candidates.Select((candidate, index) => string.Format("{0}:{1}/{2:F3}",
            index + 1, candidate.SourceName, candidate.MatchScore)));

    private static string DescribeOptions(AutoMatchOptions options) => string.Join(", ",
        AutoMatchOptions.Names.Select(name =>
        {
            var option = options.Get(name);
            return string.Format("{0}:enabled={1},mode={2},overwrite={3}", name,
                option.Enabled, option.WriteMode, option.Overwrite);
        }));

    private static string DescribeTagData(TagData tags)
    {
        var fields = new List<string>();
        if (tags.Title != null) fields.Add("Title");
        if (tags.Artist != null) fields.Add("Artist");
        if (tags.Album != null) fields.Add("Album");
        if (tags.Year.HasValue) fields.Add("Year");
        if (tags.Track.HasValue) fields.Add("Track");
        if (tags.TrackCount.HasValue) fields.Add("TrackCount");
        if (tags.Genre != null) fields.Add("Genre");
        if (tags.Comment != null) fields.Add("Comment");
        if (tags.Disc.HasValue) fields.Add("Disc");
        if (tags.DiscCount.HasValue) fields.Add("DiscCount");
        if (tags.Lyrics != null) fields.Add("Lyrics");
        if (tags.AllPictures != null && tags.AllPictures.Count > 0) fields.Add("Pictures");
        return fields.Count == 0 ? "无" : string.Join(",", fields);
    }

    private sealed class SourceSetting
    {
        public string Key { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public int Limit { get; set; }
    }

    private IReadOnlyList<SourceSetting> LoadSources(string? json, TagSourceCategory category)
    {
        var all = TagSourceCatalog.Load(json, category, _settings.WebSearchItemsLimit);
        var enabled = all.Where(s => s.Enabled).ToList();
        _logger.Debug("[标签源][批量自动匹配] 配置读取: 类别={0}, 全部={1}", category,
            TagSourceCatalog.Describe(all));
        _logger.Debug("[标签源][批量自动匹配] 实际启用顺序: 类别={0}, {1}", category,
            enabled.Count == 0 ? "无" : TagSourceCatalog.Describe(enabled));
        return enabled
            .Select(s => new SourceSetting
            {
                Key = s.Key,
                Sequence = s.Sequence,
                Limit = s.WebSearchItemsLimit > 0 && s.WebSearchItemsLimit <= 99
                    ? s.WebSearchItemsLimit : Math.Max(1, _settings.WebSearchItemsLimit)
            }).ToList();
    }

    private LyricInfo.DownloadConfig CreateLyricDownloadConfig() => new()
    {
        DownloadTranslation = _settings.LyricDownload_DownloadTrans_Enable,
        DontDownloadOriginal = _settings.LyricDownload_DownloadTrans_DontDownloadOrigLyric,
        LyricFormat = _settings.LyricDownload_DownloadTrans_LyricFormat ?? "{artist} - {title}.lrc",
        ReformatTimetag = _settings.LyricDownload_ReformatTimetag,
        RemoveTimetag = _settings.LyricDownload_RemoveTimetag,
        DeleteHeadTag = _settings.LyricDownload_DeleteHeadTag,
        DeleteBlankLines = _settings.LyricDownload_DeleteLinesOfBlankText,
        ChineseConvMode = _settings.LyricDownload_DownloadTrans_ChineseConvMode ?? "none"
    };

    private async Task<bool> SaveCoverFileAsync(MusicFile file, CoverArt cover, bool overwrite, CancellationToken ct)
    {
        var ext = cover.MimeType switch { "image/png" => ".png", "image/gif" => ".gif", "image/bmp" => ".bmp", _ => ".jpg" };
        var path = Path.Combine(file.Directory, Path.GetFileNameWithoutExtension(file.FileName) + ext);
        if (!overwrite && File.Exists(path))
        {
            _logger.Debug("[标签源][批量自动匹配] 封面文件已存在，跳过: 路径={0}", path);
            return false;
        }
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => File.WriteAllBytes(path, cover.ImageData ?? Array.Empty<byte>()), ct).ConfigureAwait(false);
        _logger.Debug("[标签源][批量自动匹配] 封面文件已写入: 路径={0}, 字节数={1}", path,
            cover.ImageData?.Length ?? 0);
        return true;
    }

    private async Task<bool> SaveLyricFileAsync(MusicFile file, LyricInfo lyric, bool overwrite, CancellationToken ct)
    {
        var format = _settings.SaveLrcFilenameFormat ?? "{artist} - {title}.lrc";
        var name = format.Replace("{artist}", file.Artist).Replace("{title}", file.Title)
            .Replace("{album}", file.Album).Replace("{track}", file.Track?.ToString("D2") ?? "");
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid.ToString(), "");
        var directory = string.IsNullOrEmpty(_settings.SaveLrcDirectory)
            ? file.Directory : _settings.SaveLrcDirectory!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        if (!overwrite && File.Exists(path))
        {
            _logger.Debug("[标签源][批量自动匹配] 歌词文件已存在，跳过: 路径={0}", path);
            return false;
        }
        var encodingName = _settings.SaveLrcFileDefaultEncoding ?? "utf-8";
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(encodingName); } catch { encoding = Encoding.UTF8; }
        var content = lyric.LrcFormatted ?? lyric.OriginalLyric ?? string.Empty;
        ct.ThrowIfCancellationRequested();
        // 复用统一的原子写：临时文件替换 + 占用重试 + 只读属性处理（overwrite 跳过判断保持不变）
        await Task.Run(() => LyricService.WriteLrcFileAtomic(path, content, encoding), ct).ConfigureAwait(false);
        _logger.Debug("[标签源][批量自动匹配] 歌词文件已写入: 路径={0}, 字符数={1}, 编码={2}",
            path, content.Length, encodingName);
        return true;
    }

    private static string? GetLyricText(LyricInfo? lyric) => lyric?.LrcFormatted ?? lyric?.OriginalLyric;

    private static MusicFile CloneFile(MusicFile file) => new()
    {
        FilePath = file.FilePath,
        Title = file.Title,
        Artist = file.Artist,
        Album = file.Album,
        Year = file.Year,
        Track = file.Track,
        TrackCount = file.TrackCount,
        Disc = file.Disc,
        DiscCount = file.DiscCount,
        Genre = file.Genre,
        Comment = file.Comment,
        Lyrics = file.Lyrics,
        HasLyrics = file.HasLyrics,
        HasCoverArt = file.HasCoverArt
    };

    private static void ApplyWrittenText(MusicFile file, TagData tags)
    {
        if (tags.Title != null) file.Title = tags.Title;
        if (tags.Artist != null) file.Artist = tags.Artist;
        if (tags.Album != null) file.Album = tags.Album;
        if (tags.Year.HasValue) file.Year = tags.Year;
        if (tags.Track.HasValue) file.Track = tags.Track;
        if (tags.TrackCount.HasValue) file.TrackCount = tags.TrackCount;
        if (tags.Disc.HasValue) file.Disc = tags.Disc;
        if (tags.DiscCount.HasValue) file.DiscCount = tags.DiscCount;
        if (tags.Genre != null) file.Genre = tags.Genre;
        if (tags.Comment != null) file.Comment = tags.Comment;
    }

    private static void ApplyReadTags(MusicFile file, TagData tags)
    {
        if (tags.Title != null) file.Title = tags.Title;
        if (tags.Artist != null) file.Artist = tags.Artist;
        if (tags.Album != null) file.Album = tags.Album;
        if (tags.Year.HasValue) file.Year = tags.Year;
        if (tags.Track.HasValue) file.Track = tags.Track;
        if (tags.TrackCount.HasValue) file.TrackCount = tags.TrackCount;
        if (tags.Disc.HasValue) file.Disc = tags.Disc;
        if (tags.DiscCount.HasValue) file.DiscCount = tags.DiscCount;
        if (tags.Genre != null) file.Genre = tags.Genre;
        if (tags.Comment != null) file.Comment = tags.Comment;
        if (tags.Lyrics != null) { file.Lyrics = tags.Lyrics; file.HasLyrics = true; }
        if (tags.CoverArtData is { Length: > 0 }) file.HasCoverArt = true;
    }
}
