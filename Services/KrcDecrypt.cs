using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MusicTagRecover.Services;

/// <summary>
/// KRC 歌词解密工具 - XOR 解密 + zlib 解压 + KRC→LRC 格式转换
/// </summary>
internal static class KrcDecrypt
{
    // XOR 解密密钥（16 字节）
    private static readonly byte[] KRC_KEY = {
        0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69
    };

    /// <summary>
    /// 解密 KRC 歌词，返回解密后的纯文本
    /// </summary>
    public static string? Decrypt(string? base64Content)
    {
        if (string.IsNullOrEmpty(base64Content)) return null;

        try
        {
            var data = Convert.FromBase64String(base64Content);
            if (data.Length <= 4) return null;

            // 跳过前 4 字节头
            var encrypted = new byte[data.Length - 4];
            Array.Copy(data, 4, encrypted, 0, encrypted.Length);

            // XOR 解密
            for (int i = 0; i < encrypted.Length; i++)
                encrypted[i] = (byte)(encrypted[i] ^ KRC_KEY[i % 16]);

            // zlib 解压
            return DecompressZlib(encrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解密 KRC 并解析为 LRC 格式，同时提取翻译歌词
    /// </summary>
    public static (string? lyric, string? tlyric)? DecryptAndParse(string? base64Content)
    {
        var plainText = Decrypt(base64Content);
        if (string.IsNullOrEmpty(plainText)) return null;

        return ParseKrcToLrc(plainText);
    }

    /// <summary>
    /// 将解密后的 KRC 纯文本解析为 LRC 格式
    /// KRC 行格式: [(startMs,durationMs)]lyric text
    /// 逐字时间:   &lt;offsetMs,durationMs,0&gt;
    /// </summary>
    private static (string lyric, string? tlyric) ParseKrcToLrc(string krcText)
    {
        var lines = krcText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var lyricSb = new StringBuilder();

        // 先尝试提取翻译歌词（按行索引匹配）
        var translationLines = ExtractTranslationLines(lines);

        // 收集原歌词行的时间戳和文本（按顺序）
        var timeTags = new List<int>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // 跳过元数据标签
            if (trimmed.StartsWith("[id:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[au:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[al:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[sign:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[songinfo:", StringComparison.OrdinalIgnoreCase))
                continue;

            // 跳过 [language:...] 标签
            if (trimmed.StartsWith("[language:", StringComparison.OrdinalIgnoreCase))
                continue;

            // 匹配 KRC 行时间标签 [(startMs,durationMs)]
            var match = Regex.Match(trimmed, @"^\[(\d+),(\d+)\]");
            if (!match.Success) continue;

            var startMs = int.Parse(match.Groups[1].Value);
            var content = trimmed.Substring(match.Length);

            // 移除逐字时间标签 <offsetMs,durationMs,0>
            var text = Regex.Replace(content, @"<\d+,\d+,\d+>", "");

            // 去除 HTML 实体
            text = DecodeHtmlEntities(text);

            if (string.IsNullOrWhiteSpace(text)) continue;

            // 转换为 LRC 时间格式
            var ts = TimeSpan.FromMilliseconds(startMs);
            lyricSb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]{text.Trim()}");
            timeTags.Add(startMs);
        }

        // 构建翻译歌词 LRC（按行索引匹配时间戳）
        string? tlyric = null;
        if (translationLines != null && translationLines.Count > 0)
        {
            var tSb = new StringBuilder();
            var count = Math.Min(translationLines.Count, timeTags.Count);
            for (int i = 0; i < count; i++)
            {
                var text = translationLines[i];
                if (string.IsNullOrWhiteSpace(text)) continue;
                var ts = TimeSpan.FromMilliseconds(timeTags[i]);
                tSb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]{text}");
            }
            var result = tSb.ToString().TrimEnd();
            if (result.Length > 0) tlyric = result;
        }

        return (lyricSb.ToString().TrimEnd(), tlyric);
    }

    /// <summary>
    /// 从 [language:BASE64] 标签中提取翻译歌词行列表
    /// JSON 格式: {"content":[{"type":0/1,"language":0,"lyricContent":[["line1"],...]}]}
    /// type=0 罗马音, type=1 翻译
    /// </summary>
    private static List<string>? ExtractTranslationLines(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("[language:", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(trimmed, @"^\[language:(.+)\]$");
            if (!match.Success) continue;

            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
                var obj = JObject.Parse(json);
                var content = obj["content"];
                if (content == null) continue;

                // 找 type=1 的条目（翻译）
                foreach (var entry in content)
                {
                    if (entry["type"]?.ToString() != "1") continue;

                    var lyricContent = entry["lyricContent"];
                    if (lyricContent == null) continue;

                    var result = new List<string>();
                    foreach (var lineParts in lyricContent)
                    {
                        var parts = lineParts.Select(p => p.ToString()).ToArray();
                        result.Add(DecodeHtmlEntities(string.Join("", parts)).Trim());
                    }

                    if (result.Count > 0) return result;
                }
            }
            catch
            {
                // 解析失败跳过
            }
        }

        return null;
    }

    private static string DecodeHtmlEntities(string text)
    {
        return text
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#039;", "'")
            .Replace("&nbsp;", " ");
    }

    private static string DecompressZlib(byte[] data)
    {
        // zlib 格式：2 字节头 + deflate 数据
        // 跳过 zlib header
        using var ms = new MemoryStream(data, 2, data.Length - 2);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        ds.CopyTo(result);
        return Encoding.UTF8.GetString(result.ToArray());
    }
}
