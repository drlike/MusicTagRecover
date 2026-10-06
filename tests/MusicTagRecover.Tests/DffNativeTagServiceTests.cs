using System.IO;
using MusicTagRecover.Services;
using MusicTagRecover.Tests.Infrastructure;
using MusicTagRecover.Utils;
using Xunit;

namespace MusicTagRecover.Tests;

/// <summary>
/// DFF 原生 DIIN/COMT 字段操作单元测试：
/// 正常读写删、批量执行、权限拦截（只读文件）、异常场景（不存在/非DFF/非法ID/索引越界）、
/// DSD 音频块完整性与容器结构校验。
/// </summary>
public sealed class DffNativeTagServiceTests : IClassFixture<DffTestFixture>
{
    private readonly DffTestFixture _fx;
    private readonly DffNativeTagService _service = new(new NullLoggerService());

    public DffNativeTagServiceTests(DffTestFixture fx) => _fx = fx;

    private DffCommtComment SampleComment(string text = "测试注释内容")
        => new()
        {
            Year = 2026, Month = 10, Day = 10, Hour = 12, Minute = 30, Second = 0,
            CommentType = 0, CommentReference = 0, Text = text,
        };

    // ================= DIIN 正常场景 =================

    [Fact]
    public void ReadDiinFields_NoDiinChunk_ReturnsEmpty()
    {
        var file = _fx.CreateFile();
        Assert.Empty(_service.ReadDiinFields(file));
    }

    [Fact]
    public void WriteDiinField_NewField_ReadBackAndDsdUnchanged()
    {
        var file = _fx.CreateFile();
        var hash = DffTestFixture.DsdSha256(file);

        Assert.True(_service.WriteDiinField(file, "DITI", "原生标题", out var err));
        Assert.Null(err);

        var fields = _service.ReadDiinFields(file);
        var diti = Assert.Single(fields);
        Assert.Equal("DITI", diti.Id);
        Assert.Equal("原生标题", diti.Text);

        Assert.Equal(hash, DffTestFixture.DsdSha256(file));
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }

    [Fact]
    public void WriteDiinField_MultipleFields_AllReadable()
    {
        var file = _fx.CreateFile();
        Assert.True(_service.WriteDiinField(file, "DITI", "标题", out _));
        Assert.True(_service.WriteDiinField(file, "DIAR", "艺术家", out _));
        Assert.True(_service.WriteDiinField(file, "DGEN", "流派", out _));

        var fields = _service.ReadDiinFields(file);
        Assert.Equal(3, fields.Count);
        Assert.Contains(fields, f => f.Id == "DITI" && f.Text == "标题");
        Assert.Contains(fields, f => f.Id == "DIAR" && f.Text == "艺术家");
        Assert.Contains(fields, f => f.Id == "DGEN" && f.Text == "流派");
    }

    [Fact]
    public void WriteDiinField_ExistingField_IsUpdated()
    {
        var file = _fx.CreateFile();
        _service.WriteDiinField(file, "DITI", "旧标题", out _);
        var hash = DffTestFixture.DsdSha256(file);

        Assert.True(_service.WriteDiinField(file, "DITI", "新标题", out _));

        var diti = Assert.Single(_service.ReadDiinFields(file));
        Assert.Equal("新标题", diti.Text);
        Assert.Equal(hash, DffTestFixture.DsdSha256(file));
    }

    [Fact]
    public void DeleteDiinField_Existing_RemovedAndChunkGoneWhenEmpty()
    {
        var file = _fx.CreateFile();
        _service.WriteDiinField(file, "DITI", "标题", out _);
        Assert.Contains(DffTestFixture.ListChunks(file), c => c.Id == "DIIN");

        Assert.True(_service.DeleteDiinField(file, "DITI", out _));

        Assert.Empty(_service.ReadDiinFields(file));
        Assert.DoesNotContain(DffTestFixture.ListChunks(file), c => c.Id == "DIIN");
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }

    [Fact]
    public void DeleteDiinField_NotPresent_SucceedsIdempotently()
    {
        var file = _fx.CreateFile();
        // 无 DIIN chunk 时删除也应成功（目标状态已达成）
        Assert.True(_service.DeleteDiinField(file, "DITI", out var err));
        Assert.Null(err);
    }

    [Fact]
    public void DeleteDiinField_OneOfMultiple_OnlyTargetRemoved()
    {
        var file = _fx.CreateFile();
        _service.WriteDiinField(file, "DITI", "标题", out _);
        _service.WriteDiinField(file, "DIAR", "艺术家", out _);

        _service.DeleteDiinField(file, "DITI", out _);

        var fields = _service.ReadDiinFields(file);
        Assert.Single(fields);
        Assert.Equal("DIAR", fields[0].Id);
    }

    [Fact]
    public void WriteDiinField_EmptyText_PersistedAsEmpty()
    {
        var file = _fx.CreateFile();
        Assert.True(_service.WriteDiinField(file, "DITI", "", out _));
        Assert.Equal("", Assert.Single(_service.ReadDiinFields(file)).Text);
    }

    // ================= COMT 正常场景 =================

    [Fact]
    public void ReadCommtComments_NoChunk_ReturnsEmpty()
    {
        var file = _fx.CreateFile();
        Assert.Empty(_service.ReadCommtComments(file));
    }

    [Fact]
    public void AddCommtComment_ReadBackAllProperties()
    {
        var file = _fx.CreateFile();
        var hash = DffTestFixture.DsdSha256(file);
        var comment = SampleComment("SACD 抓轨注释");

        Assert.True(_service.AddCommtComment(file, comment, out _));

        var comments = _service.ReadCommtComments(file);
        var actual = Assert.Single(comments);
        Assert.Equal(2026, actual.Year);
        Assert.Equal(10, actual.Month);
        Assert.Equal(10, actual.Day);
        Assert.Equal(12, actual.Hour);
        Assert.Equal(30, actual.Minute);
        Assert.Equal(0, actual.Second);
        Assert.Equal(0, actual.CommentType);
        Assert.Equal("SACD 抓轨注释", actual.Text);
        Assert.Equal(hash, DffTestFixture.DsdSha256(file));
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }

    [Fact]
    public void AddCommtComment_Multiple_OrderPreserved()
    {
        var file = _fx.CreateFile();
        _service.AddCommtComment(file, SampleComment("第一条"), out _);
        _service.AddCommtComment(file, SampleComment("第二条"), out _);
        _service.AddCommtComment(file, SampleComment("第三条"), out _);

        var comments = _service.ReadCommtComments(file);
        Assert.Equal(3, comments.Count);
        Assert.Equal("第一条", comments[0].Text);
        Assert.Equal("第二条", comments[1].Text);
        Assert.Equal("第三条", comments[2].Text);
    }

    [Fact]
    public void WriteCommtComment_ModifyIndex_OnlyTargetChanged()
    {
        var file = _fx.CreateFile();
        _service.AddCommtComment(file, SampleComment("第一条"), out _);
        _service.AddCommtComment(file, SampleComment("第二条"), out _);

        var modified = SampleComment("修改后的第二条");
        Assert.True(_service.WriteCommtComment(file, 1, modified, out _));

        var comments = _service.ReadCommtComments(file);
        Assert.Equal(2, comments.Count);
        Assert.Equal("第一条", comments[0].Text);
        Assert.Equal("修改后的第二条", comments[1].Text);
    }

    [Fact]
    public void DeleteCommtComment_LastOne_RemovesWholeChunk()
    {
        var file = _fx.CreateFile();
        _service.AddCommtComment(file, SampleComment(), out _);
        Assert.Contains(DffTestFixture.ListChunks(file), c => c.Id == "COMT");

        Assert.True(_service.DeleteCommtComment(file, 0, out _));
        Assert.Empty(_service.ReadCommtComments(file));
        Assert.DoesNotContain(DffTestFixture.ListChunks(file), c => c.Id == "COMT");
    }

    [Fact]
    public void DeleteCommtComment_MiddleOne_ShiftsIndices()
    {
        var file = _fx.CreateFile();
        _service.AddCommtComment(file, SampleComment("A"), out _);
        _service.AddCommtComment(file, SampleComment("B"), out _);
        _service.AddCommtComment(file, SampleComment("C"), out _);

        _service.DeleteCommtComment(file, 1, out _);

        var comments = _service.ReadCommtComments(file);
        Assert.Equal(2, comments.Count);
        Assert.Equal("A", comments[0].Text);
        Assert.Equal("C", comments[1].Text);
    }

    // ================= 批量场景 =================

    [Fact]
    public void WriteDiinFieldBatch_AllFilesSucceed()
    {
        var files = _fx.CreateFiles(3);
        var result = _service.WriteDiinFieldBatch(files, "DITI", "批量标题");

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Succeeded);
        Assert.True(result.AllSucceeded);
        foreach (var f in files)
            Assert.Equal("批量标题", Assert.Single(_service.ReadDiinFields(f)).Text);
    }

    [Fact]
    public void DeleteDiinFieldBatch_AllFilesSucceed()
    {
        var files = _fx.CreateFiles(3);
        _service.WriteDiinFieldBatch(files, "DITI", "批量标题");

        var result = _service.DeleteDiinFieldBatch(files, "DITI");

        Assert.True(result.AllSucceeded);
        foreach (var f in files) Assert.Empty(_service.ReadDiinFields(f));
    }

    [Fact]
    public void AddCommtCommentBatch_EveryFileGetsComment()
    {
        var files = _fx.CreateFiles(3);
        var result = _service.AddCommtCommentBatch(files, SampleComment("批量注释"));

        Assert.True(result.AllSucceeded);
        foreach (var f in files)
            Assert.Equal("批量注释", Assert.Single(_service.ReadCommtComments(f)).Text);
    }

    [Fact]
    public void Batch_OneFileFails_PartialResultWithFailureDetail()
    {
        var files = (List<string>)_fx.CreateFiles(3);
        // 第二个文件模拟权限拦截
        File.SetAttributes(files[1], FileAttributes.ReadOnly);
        try
        {
            var result = _service.WriteDiinFieldBatch(files, "DITI", "批量标题");

            Assert.Equal(3, result.Total);
            Assert.Equal(2, result.Succeeded);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(files[1], failure.FilePath);
            Assert.False(string.IsNullOrWhiteSpace(failure.Error));
            // 成功的文件已写入
            Assert.Equal("批量标题", Assert.Single(_service.ReadDiinFields(files[0])).Text);
            Assert.Equal("批量标题", Assert.Single(_service.ReadDiinFields(files[2])).Text);
            // 被拦截文件保持原状（无 DIIN）
            Assert.Empty(_service.ReadDiinFields(files[1]));
        }
        finally
        {
            File.SetAttributes(files[1], FileAttributes.Normal);
        }
    }

    [Fact]
    public void ClearAllCommtBatch_RemovesCommentsFromAllFiles()
    {
        var files = (List<string>)_fx.CreateFiles(2);
        _service.AddCommtCommentBatch(files, SampleComment("A"));
        _service.AddCommtCommentBatch(files, SampleComment("B"));

        var result = _service.ClearAllCommtBatch(files);

        Assert.True(result.AllSucceeded);
        foreach (var f in files)
        {
            Assert.Empty(_service.ReadCommtComments(f));
            Assert.DoesNotContain(DffTestFixture.ListChunks(f), c => c.Id == "COMT");
        }
    }

    // ================= 权限拦截场景 =================

    [Fact]
    public void WriteDiinField_ReadOnlyFile_FailsAndLeavesFileIntact()
    {
        var file = _fx.CreateFile();
        var beforeHash = DffTestFixture.DsdSha256(file);
        var beforeBytes = File.ReadAllBytes(file);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            bool ok = _service.WriteDiinField(file, "DITI", "不应写入", out var err);
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        // 文件字节级未变 + 音频块完整 + 容器可遍历
        Assert.Equal(beforeBytes, File.ReadAllBytes(file));
        Assert.Equal(beforeHash, DffTestFixture.DsdSha256(file));
        Assert.Empty(_service.ReadDiinFields(file));
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }

    [Fact]
    public void DeleteDiinField_ReadOnlyFile_Fails()
    {
        var file = _fx.CreateFile();
        _service.WriteDiinField(file, "DITI", "标题", out _);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Assert.False(_service.DeleteDiinField(file, "DITI", out var err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        // 字段仍在
        Assert.Equal("标题", Assert.Single(_service.ReadDiinFields(file)).Text);
    }

    // ================= 异常 / 参数校验场景 =================

    [Fact]
    public void WriteDiinField_FileMissing_FalseWithError()
    {
        var ok = _service.WriteDiinField(
            Path.Combine(_fx.Directory, "missing.dff"), "DITI", "x", out var err);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void WriteDiinField_NonDffExtension_FalseWithError()
    {
        var mp3 = Path.Combine(_fx.Directory, "song.mp3");
        File.WriteAllBytes(mp3, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        Assert.False(_service.WriteDiinField(mp3, "DITI", "x", out var err));
        Assert.Contains("DFF", err);
    }

    [Theory]
    [InlineData("")]      // 空
    [InlineData("AB")]    // 长度不足
    [InlineData("ABCDE")] // 超长
    public void WriteDiinField_InvalidId_FalseWithError(string fieldId)
    {
        var file = _fx.CreateFile();
        Assert.False(_service.WriteDiinField(file, fieldId, "x", out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
        Assert.Empty(_service.ReadDiinFields(file));
    }

    [Fact]
    public void WriteDiinField_NonAsciiId_FalseWithError()
    {
        var file = _fx.CreateFile();
        Assert.False(_service.WriteDiinField(file, "DI中N", "x", out var err));
        Assert.Contains("ASCII", err);
    }

    [Fact]
    public void Batch_InvalidFieldId_ThrowsArgumentException()
    {
        var files = _fx.CreateFiles(2);
        Assert.Throws<ArgumentException>(() =>
            _service.WriteDiinFieldBatch(files, "XX", "x"));
        Assert.Throws<ArgumentException>(() =>
            _service.DeleteDiinFieldBatch(files, "BADID"));
    }

    [Fact]
    public void WriteCommtComment_NegativeIndex_FalseWithError()
    {
        var file = _fx.CreateFile();
        Assert.False(_service.WriteCommtComment(file, -1, SampleComment(), out var err));
        Assert.False(_service.DeleteCommtComment(file, -1, out err));
    }

    [Fact]
    public void WriteCommtComment_IndexOutOfRange_FalseWithError()
    {
        var file = _fx.CreateFile();
        // 无 COMT chunk 时修改索引 0 应失败而非破坏文件
        Assert.False(_service.WriteCommtComment(file, 0, SampleComment(), out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
        Assert.Empty(_service.ReadCommtComments(file));
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }

    [Fact]
    public void DeleteCommtComment_IndexOutOfRange_FalseWithError()
    {
        var file = _fx.CreateFile();
        Assert.False(_service.DeleteCommtComment(file, 5, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void AddCommtComment_InvalidTimestamp_ThrowsFromBatchValidate()
    {
        var files = _fx.CreateFiles(1);
        var bad = SampleComment();
        bad.Year = unchecked((ushort)99999); // 34463，超出合法年份范围，验证年份校验
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.AddCommtCommentBatch(files, bad));
    }

    [Fact]
    public void ReadDiinFields_FileMissing_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            _service.ReadDiinFields(Path.Combine(_fx.Directory, "nope.dff")));
    }

    // ================= 往返与完整性 =================

    [Fact]
    public void MixedOpsRoundTrip_DsdBlockNeverChanges()
    {
        var file = _fx.CreateFile();
        var hash = DffTestFixture.DsdSha256(file);

        _service.WriteDiinField(file, "DITI", "标题", out _);
        _service.WriteDiinField(file, "DIAR", "艺术家", out _);
        _service.AddCommtComment(file, SampleComment(), out _);
        Assert.Equal(hash, DffTestFixture.DsdSha256(file));

        _service.WriteDiinField(file, "DITI", "改标题", out _);
        _service.WriteCommtComment(file, 0, SampleComment("改注释"), out _);
        Assert.Equal(hash, DffTestFixture.DsdSha256(file));

        _service.DeleteDiinField(file, "DIAR", out _);
        _service.DeleteCommtComment(file, 0, out _);
        _service.DeleteDiinField(file, "DITI", out _);

        Assert.Equal(hash, DffTestFixture.DsdSha256(file));
        Assert.True(DffTestFixture.ContainerWalkable(file));
        Assert.Empty(_service.ReadDiinFields(file));
        Assert.Empty(_service.ReadCommtComments(file));
    }

    [Fact]
    public void WriteDiinField_OddLengthText_PaddingDoesNotCorruptContainer()
    {
        var file = _fx.CreateFile();
        // 奇数 UTF-8 字节长度文本，验证 chunk 偶数对齐
        Assert.True(_service.WriteDiinField(file, "DITI", "abc", out _));
        Assert.Equal("abc", Assert.Single(_service.ReadDiinFields(file)).Text);
        Assert.True(DffTestFixture.ContainerWalkable(file));
    }
}
