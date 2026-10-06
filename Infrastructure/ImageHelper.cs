using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicTagRecover.Infrastructure;

/// <summary>
/// WPF 原生图像辅助：byte[] ↔ BitmapSource，解码时按最长边降采样（替代旧 GDI+ 缩略图）。
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// 将图片字节解码为冻结的 BitmapSource。decodeMax &gt; 0 时按最长边缩到该像素以内。
    /// </summary>
    public static BitmapSource? ToBitmapSource(byte[]? data, int decodeMax = 0)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            // 第一遍只取帧头尺寸（不强制全像素解码）
            int pw, ph;
            using (var probe = new MemoryStream(data, false))
            {
                var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.OnLoad);
                pw = frame.PixelWidth;
                ph = frame.PixelHeight;
            }
            if (pw <= 0 || ph <= 0) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.None;
            if (decodeMax > 0 && (pw > decodeMax || ph > decodeMax))
            {
                // 只设置较长边，等比缩放由解码器完成
                if (pw >= ph) bmp.DecodePixelWidth = decodeMax;
                else bmp.DecodePixelHeight = decodeMax;
            }
            bmp.StreamSource = new MemoryStream(data, false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>将 BitmapSource 按目标格式编码为字节。</summary>
    public static byte[]? Encode(BitmapSource source, string mimeType = "image/jpeg",
        int quality = 90)
    {
        try
        {
            BitmapEncoder encoder = mimeType.ToLowerInvariant() switch
            {
                "image/png" => new PngBitmapEncoder(),
                _ => new JpegBitmapEncoder { QualityLevel = quality },
            };
            var frame = BitmapFrame.Create(source);
            encoder.Frames.Add(frame);
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>等比缩放 BitmapSource 到最长边 maxSize，返回冻结图像。</summary>
    public static BitmapSource? Resize(byte[] data, int maxSize)
    {
        var src = ToBitmapSource(data);
        if (src == null) return null;
        double scale = maxSize / (double)Math.Max(src.PixelWidth, src.PixelHeight);
        if (scale >= 1) return src;
        var transformed = new TransformedBitmap(src,
            new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }
}
