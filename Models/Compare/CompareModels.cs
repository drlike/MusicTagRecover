namespace MusicTagRecover.Models.Compare;

/// <summary>
/// 文件比对匹配规则（三选一，用户单选切换）。
/// </summary>
public enum CompareMatchRule
{
    /// <summary>规则1：基础属性 — 「文件名+扩展名完全一致」且「文件大小完全相等」。</summary>
    NameAndSize = 1,

    /// <summary>规则2：全局唯一 — MD5 哈希值完全一致。</summary>
    Md5 = 2,

    /// <summary>规则3：音视频专属 — 内嵌元数据标签的「标题+艺术家」完全匹配（仅音视频格式）。</summary>
    MediaTag = 3,
}

/// <summary>
/// 比对文件记录状态常量（对应 compare_files.status 列）。
/// 删除仅做状态标记，不物理抹除记录。
/// </summary>
public static class CompareFileStatus
{
    /// <summary>正常（扫描在库）</summary>
    public const int Normal = 0;

    /// <summary>用户已删除（移入回收站，可恢复）</summary>
    public const int UserDeleted = 1;

    /// <summary>二次扫描时文件已不存在</summary>
    public const int Missing = 2;
}

/// <summary>
/// 比对模块扫描采集的文件元数据（对应 SQLite 表 compare_files）。
/// 全量入库 + 索引，二次扫描时按 路径+大小+修改时间 命中缓存直接复用 MD5/标签，实现提速。
/// </summary>
public class CompareFileRecord
{
    /// <summary>数据库自增主键</summary>
    public long Id { get; set; }

    /// <summary>完整路径（唯一索引，大小写不敏感判重）</summary>
    public string FullPath { get; set; } = "";

    /// <summary>所属扫描根目录</summary>
    public string RootDir { get; set; } = "";

    /// <summary>所在目录</summary>
    public string Dir { get; set; } = "";

    /// <summary>文件名（不含扩展名）</summary>
    public string Name { get; set; } = "";

    /// <summary>扩展名（统一小写，含点）</summary>
    public string Ext { get; set; } = "";

    /// <summary>文件大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>最后修改时间 ticks（缓存失效判定依据）</summary>
    public long MtimeTicks { get; set; }

    /// <summary>MD5 哈希（规则2懒计算，命中缓存直接复用）</summary>
    public string? Md5 { get; set; }

    /// <summary>内嵌标签-标题（规则3懒计算，仅音视频）</summary>
    public string? Title { get; set; }

    /// <summary>内嵌标签-艺术家（规则3懒计算，仅音视频）</summary>
    public string? Artist { get; set; }

    /// <summary>是否音视频扩展名</summary>
    public bool IsMedia { get; set; }

    /// <summary>状态：0 正常 / 1 用户删除 / 2 扫描缺失</summary>
    public int Status { get; set; }

    /// <summary>删除时间 ticks（未删除为 null）</summary>
    public long? DeletedAtTicks { get; set; }

    /// <summary>最近一次确认存在的时间 ticks</summary>
    public long ScannedAtTicks { get; set; }

    /// <summary>含扩展名的文件名</summary>
    public string FileName => Name + Ext;

    /// <summary>最后修改时间</summary>
    public DateTime ModifiedTime => new DateTime(MtimeTicks, DateTimeKind.Local);
}

/// <summary>
/// 一条比对命中结果（结果列表中的一行）。
/// 语义约定：源文件 = 待删除方（命中后移入回收站），目标文件 = 保留方（仅参考，不删除）。
/// </summary>
public class CompareMatchResult
{
    /// <summary>源文件（待删除方）所在完整目录路径</summary>
    public string SourceDir { get; set; } = "";

    /// <summary>源文件名称（含扩展名）</summary>
    public string SourceName { get; set; } = "";

    /// <summary>源文件大小（字节）</summary>
    public long SourceSize { get; set; }

    /// <summary>源文件完整路径（删除操作定位用）</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>目标文件（保留方）所在完整目录路径</summary>
    public string TargetDir { get; set; } = "";

    /// <summary>目标文件名称（含扩展名）</summary>
    public string TargetName { get; set; } = "";

    /// <summary>目标文件大小（字节，与源大小并列展示供直观对比）</summary>
    public long TargetSize { get; set; }

    /// <summary>匹配命中的目标文件完整路径</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>当前使用的匹配规则类型名称</summary>
    public string RuleName { get; set; } = "";

    /// <summary>源文件最后修改时间</summary>
    public DateTime LastModified { get; set; }
}

/// <summary>
/// 删除操作日志记录（对应 SQLite 表 compare_delete_log）。
/// 完整留存操作时间戳、操作人、操作路径，用于删除历史追溯查询。
/// </summary>
public class CompareDeleteLogRecord
{
    public long Id { get; set; }

    /// <summary>操作时间</summary>
    public DateTime OpTime { get; set; }

    /// <summary>操作人（机器名\用户名）</summary>
    public string Operator { get; set; } = "";

    /// <summary>操作类型：file / folder / empty_dir / restore</summary>
    public string OpType { get; set; } = "";

    /// <summary>操作路径（原路径）</summary>
    public string Path { get; set; } = "";

    /// <summary>备注详情</summary>
    public string Detail { get; set; } = "";
}
