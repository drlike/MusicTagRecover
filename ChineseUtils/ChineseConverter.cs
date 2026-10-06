using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MusicTagRecover.ChineseUtils
{
    /// <summary>
    /// 中文简繁转换
    /// 使用 CJK 统一汉字区块 (U+4E00-U+9FA5) 的扁平 char[] 查表，
    /// 并支持词组（lexeme）级别的整体转换。
    /// </summary>
    internal static class ChineseConverter
    {
        private static readonly char[] S2T_Lookup;
        private static readonly char[] T2S_Lookup;
        private static readonly Dictionary<string, string> S2T_Lexemes;
        private static readonly Dictionary<string, string> T2S_Lexemes;
        private static readonly object _lock = new();
        private static bool _initialized;

        static ChineseConverter()
        {
            S2T_Lookup = new char[0x9FA5 - 0x4E00 + 1]; // 20902
            T2S_Lookup = new char[0x9FA5 - 0x4E00 + 1];
            S2T_Lexemes = new Dictionary<string, string>(StringComparer.Ordinal);
            T2S_Lexemes = new Dictionary<string, string>(StringComparer.Ordinal);
            Initialize();
        }

        private static void Initialize()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;

                // Decompress and build S2T lookup table
                var s2tChars = DecompressMapping(ChineseMappingData.T2S_Compressed);
                BuildLookupTable(S2T_Lookup, s2tChars);

                // Decompress and build T2S lookup table
                var t2sChars = DecompressMapping(ChineseMappingData.S2T_Compressed);
                BuildLookupTable(T2S_Lookup, t2sChars);

                // Load lexemes (词组映射)
                LoadLexemes(ChineseMappingData.S2T_Lexemes, S2T_Lexemes);
                LoadLexemes(ChineseMappingData.T2S_Lexemes, T2S_Lexemes);

                _initialized = true;
            }
        }

        private static string DecompressMapping(string compressedBase64)
        {
            byte[] compressed = Convert.FromBase64String(compressedBase64);
            using var ms = new MemoryStream(compressed);
            using var gzip = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// 构建平坦查表数组。
        /// rulesStub[i] 对应 CJK 字符 U+4E00+i 的转换结果，
        /// 若无需转换则 rulesStub[i] == 原字符。
        /// </summary>
        private static void BuildLookupTable(char[] table, string mappingChars)
        {
            int baseCp = 0x4E00;
            for (int i = 0; i < table.Length && i < mappingChars.Length; i++)
            {
                table[i] = mappingChars[i];
            }
            // Fill any remaining positions with identity
            for (int i = mappingChars.Length; i < table.Length; i++)
            {
                table[i] = (char)(baseCp + i);
            }
        }

        private static void LoadLexemes(string[] lines, Dictionary<string, string> dict)
        {
            if (lines == null) return;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq > 0 && eq < line.Length - 1)
                {
                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    if (!string.IsNullOrEmpty(key))
                        dict[key] = value;
                }
            }
        }

        /// <summary>
        /// 将字符串从简体中文转换为繁体中文
        /// </summary>
        public static string SimplifiedToTraditional(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return ConvertText(text, S2T_Lookup, S2T_Lexemes);
        }

        /// <summary>
        /// 将字符串从繁体中文转换为简体中文
        /// </summary>
        public static string TraditionalToSimplified(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return ConvertText(text, T2S_Lookup, T2S_Lexemes);
        }

        /// <summary>
        /// 执行转换：优先匹配词组（lexeme），再逐字查表。
        /// </summary>
        private static string ConvertText(string text, char[] lookup, Dictionary<string, string> lexemes)
        {
            int len = text.Length;
            var sb = new StringBuilder(len);

            for (int i = 0; i < len; )
            {
                bool matched = false;

                // Try to match a lexeme (词组) starting at the current position
                if (lexemes.Count > 0)
                {
                    int maxMatchLen = Math.Min(len - i, 32); // limit for performance
                    for (int m = maxMatchLen; m >= 2; m--) // prefer longest match
                    {
                        string sub = text.Substring(i, m);
                        if (lexemes.TryGetValue(sub, out var replacement))
                        {
                            sb.Append(replacement);
                            i += m;
                            matched = true;
                            break;
                        }
                    }
                }

                if (!matched)
                {
                    // Character-by-character lookup
                    char c = text[i];
                    if (c >= 0x4E00 && c <= 0x9FA5)
                    {
                        char converted = lookup[c - 0x4E00];
                        sb.Append(converted != '\0' ? converted : c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    i++;
                }
            }

            return sb.ToString();
        }
    }
}
