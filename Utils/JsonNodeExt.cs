using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicTagRecover.Utils;

/// <summary>
/// JsonNode 扩展方法 — 提供 Newtonsoft.Json JObject/JToken 的等价便捷访问。
/// 用于无类型动态 JSON 解析（网络 API 响应）场景。
/// </summary>
internal static class JsonNodeExt
{
    /// <summary>按键安全取子节点（不存在或非对象返回 null）。</summary>
    public static JsonNode? Get(this JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>按键取字符串值（不存在返回 null）。</summary>
    public static string? Str(this JsonNode? node, string key)
        => node.Get(key)?.ToString();

    /// <summary>按键取布尔值（不存在或类型不匹配返回 null）。</summary>
    public static bool? Bool(this JsonNode? node, string key)
    {
        var v = node.Get(key);
        if (v == null || JsonSerializer.SerializeToElement(v).ValueKind == JsonValueKind.Null) return null;
        try { return v.GetValue<bool>(); } catch { return null; }
    }

    /// <summary>按键取 int 值（不存在或类型不匹配返回 null）。</summary>
    public static int? Int(this JsonNode? node, string key)
    {
        var v = node.Get(key);
        if (v == null || JsonSerializer.SerializeToElement(v).ValueKind == JsonValueKind.Null) return null;
        try { return v.GetValue<int>(); } catch { return null; }
    }

    /// <summary>按键取 long 值（不存在或类型不匹配返回 null）。</summary>
    public static long? Long(this JsonNode? node, string key)
    {
        var v = node.Get(key);
        if (v == null || JsonSerializer.SerializeToElement(v).ValueKind == JsonValueKind.Null) return null;
        try { return v.GetValue<long>(); } catch { return null; }
    }

    /// <summary>判断节点是否为 JSON null（含 C# null）。</summary>
    public static bool IsNull(this JsonNode? node)
        => node == null || JsonSerializer.SerializeToElement(node).ValueKind == JsonValueKind.Null;

    /// <summary>判断节点是否为非 null 值。</summary>
    public static bool NotNull(this JsonNode? node)
        => !node.IsNull();
}
