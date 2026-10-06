namespace MusicTagRecover.Models;

/// <summary>
/// 封面下载结果，包含封面数据或验证失败原因
/// </summary>
public class CoverDownloadResult
{
    public CoverArt? Cover { get; set; }
    public string? ValidationError { get; set; }
    public bool Success => Cover != null;
}

/// <summary>
/// 封面图片信息，包含图片数据、尺寸和MIME类型
/// </summary>
public class CoverArt
{
    public byte[]? ImageData { get; set; }
    public string? MimeType { get; set; }

    /// <summary>封面图片类型（与 TagLib.PictureType 数值兼容），null 按 FrontCover 处理。</summary>
    public CoverPictureType? PictureType { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSizeBytes => ImageData?.Length ?? 0;
    public bool HasImage => ImageData is { Length: > 0 };

    /// <summary>
    /// 封面格式/大小限制配置
    /// </summary>
    public class LimitsConfig
    {
        /// <summary>支持的图片格式（如 "jpg,png,bmp"）</summary>
        public string FormatLimits { get; set; } = "jpg,jpeg,png,bmp,gif";

        /// <summary>最大分辨率（宽或高），null 表示不限制</summary>
        public int? MaxResolution { get; set; }

        /// <summary>最大文件大小 (KB)，null 表示不限制</summary>
        public int? MaxSizeKB { get; set; }

        /// <summary>是否覆盖已有封面</summary>
        public bool OverwriteExisting { get; set; } = true;
    }

    /// <summary>
    /// 从文件加载封面图片
    /// </summary>
    public static CoverArt FromFile(string imagePath)
    {
        var data = File.ReadAllBytes(imagePath);
        var mime = GetMimeType(imagePath);
        return new CoverArt { ImageData = data, MimeType = mime };
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// 验证是否在限制范围内
    /// </summary>
    public bool Validate(LimitsConfig limits, out string errorMessage)
    {
        if (!HasImage)
        {
            errorMessage = "无图片数据";
            return false;
        }

        var ext = MimeType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/bmp" => "bmp",
            "image/gif" => "gif",
            _ => ""
        };

        if (string.IsNullOrEmpty(ext) || limits.FormatLimits.IndexOf(ext, StringComparison.OrdinalIgnoreCase) < 0)
        {
            var displayMime = string.IsNullOrEmpty(ext) ? MimeType ?? "未知" : ext.ToUpperInvariant();
            errorMessage = $"格式不支持：{displayMime}，允许的格式：{limits.FormatLimits}";
            return false;
        }

        var sizeKB = FileSizeBytes / 1024;
        if (limits.MaxResolution is > 0 && (Width > limits.MaxResolution || Height > limits.MaxResolution))
        {
            var max = limits.MaxResolution.Value;
            var dimension = Width > max && Height > max
                ? $"宽{Width}和高{Height}均"
                : Width > max ? $"宽{Width}" : $"高{Height}";
            errorMessage = $"分辨率超出限制：{dimension}超过最大值{max}（当前{Width}×{Height}）";
            return false;
        }

        if (limits.MaxSizeKB is > 0 && sizeKB > limits.MaxSizeKB)
        {
            errorMessage = $"文件大小超出限制：{sizeKB}KB 超过最大值{limits.MaxSizeKB}KB";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }
}
