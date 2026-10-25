using System.IO;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Integrity;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 一致性报告导出的测试（v0.6）。
///
/// 纯函数测试：Build 不碰文件系统。两节内容都必须出现——只导出一节会让
/// 收件人以为另一节没问题（这是两节合成一份报告的原因）。
/// </summary>
public class IntegrityExportTests
{
    private static ServiceOrphanScanResult FakeServices(int count = 1) => new()
    {
        ScannedCount = 836,
        SkippedCount = 792,
        Entries = Enumerable.Range(0, count).Select(i => new ServiceOrphanEntry
        {
            ServiceName = $"Svc{i}",
            DisplayName = $"测试服务{i}",
            ImagePathRaw = $"\"D:\\nexus\\app{i}\"",
            ResolvedPath = $"D:\\nexus\\app{i}",
            StartModeText = "自动",
            StateText = "运行中",
            Evidence = ["文件不存在"],
        }).ToList(),
    };

    private static AppStateScanResult FakeStates(int count = 2) => new()
    {
        CheckedCount = 113,
        Entries = Enumerable.Range(0, count).Select(i => new AppStateIssue
        {
            Kind = AppStateIssueKind.InstallLocationMissing,
            DisplayName = $"软件{i}",
            ProductCode = $"{{00000000-0000-0000-0000-{i:D12}}}",
            Evidence = ["目录不存在"],
            Suggestions = ["建议重新安装或忽略"],
        }).ToList(),
    };

    [Fact]
    public void Build_Csv_ContainsBothSections()
    {
        var csv = IntegrityReportWriter.Build(
            FakeServices(), FakeStates(), ReportFormat.Csv, "摘要文本", DateTime.Now);

        Assert.Contains("服务孤儿", csv);
        Assert.Contains("状态不一致", csv);
        Assert.Contains("Svc0", csv);
        Assert.Contains("软件0", csv);
        Assert.Contains("摘要", csv);
    }

    [Fact]
    public void Build_Csv_ContainsSummary()
    {
        var csv = IntegrityReportWriter.Build(
            FakeServices(), FakeStates(), ReportFormat.Csv, "共检查 836 个服务", DateTime.Now);

        Assert.Contains("共检查 836 个服务", csv);
    }

    [Fact]
    public void Build_Csv_CommaValueIsQuoted()
    {
        // DisplayName 带逗号 → 整段值必须加引号，否则列会错位
        var services = new ServiceOrphanScanResult
        {
            ScannedCount = 1,
            Entries = [new ServiceOrphanEntry
            {
                ServiceName = "a,b",
                DisplayName = "x",
                ImagePathRaw = "p",
                ResolvedPath = "p",
                StartModeText = "自动",
                StateText = "运行中",
            }],
        };

        var csv = IntegrityReportWriter.Build(services, FakeStates(), ReportFormat.Csv, "s", DateTime.Now);

        Assert.Contains("\"a,b\"", csv);
    }

    [Fact]
    public void Build_Html_ContainsBothTablesAndReadOnlyNotice()
    {
        var html = IntegrityReportWriter.Build(
            FakeServices(2), FakeStates(3), ReportFormat.Html, "摘要", DateTime.Now);

        Assert.Contains("<h2>服务 ImagePath 孤儿（2 条", html);
        Assert.Contains("<h2>文件与注册表状态不一致（3 条", html);
        // 只读承诺必须出现在导出件里——收件人拿到的文档要自带"不会自动清理"的说明
        Assert.Contains("不会自动清理", html);
        Assert.Contains("sc delete", html);
    }

    [Fact]
    public void Build_Html_EscapesAngleBrackets()
    {
        var services = new ServiceOrphanScanResult
        {
            ScannedCount = 1,
            Entries = [new ServiceOrphanEntry
            {
                ServiceName = "<script>alert(1)</script>",
                DisplayName = "x",
                ImagePathRaw = "p",
                ResolvedPath = "p",
                StartModeText = "自动",
                StateText = "运行中",
            }],
        };

        var html = IntegrityReportWriter.Build(services, FakeStates(), ReportFormat.Html, "s", DateTime.Now);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void SuggestFileName_HasExtensionAndTimestamp()
    {
        var csv = IntegrityReportWriter.SuggestFileName(ReportFormat.Csv, new DateTime(2026, 10, 25, 12, 0, 0));
        var html = IntegrityReportWriter.SuggestFileName(ReportFormat.Html, new DateTime(2026, 10, 25, 12, 0, 0));

        Assert.EndsWith(".csv", csv);
        Assert.EndsWith(".html", html);
        Assert.Contains("20261025-120000", csv);
    }

    [Fact]
    public void WriteTo_CreatesFileWithUtf8Bom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-intexport-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = IntegrityReportWriter.WriteTo(dir, FakeServices(), FakeStates(), ReportFormat.Csv, "s", DateTime.Now);

            Assert.True(File.Exists(path));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}