using System.IO;
using System.Text;
using RubbishCleaner.Core.Integrity;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>
/// 系统一致性报告的导出（v0.6）。
///
/// <para>风格对齐 <see cref="ReportWriter"/>：纯函数生成字符串，不碰文件系统、
/// 不弹对话框；副作用留在一个单独的 <see cref="WriteTo"/> 里。</para>
///
/// <para>内容固定两节（服务孤儿 + 文件与注册表状态不一致），因为这两个扫描是
/// 一个「检查」动作产出的两半——只导出一节会让收件人以为另一节没问题。</para>
/// </summary>
public static class IntegrityReportWriter
{
    /// <summary>建议的文件名（含时间戳）。</summary>
    public static string SuggestFileName(ReportFormat format, DateTime timestamp)
        => $"RubbishCleaner-一致性报告-{timestamp:yyyyMMdd-HHmmss}.{(format == ReportFormat.Csv ? "csv" : "html")}";

    /// <summary>生成报告文本。CSV 带 BOM 由 WriteTo 负责（Build 只产文本）。</summary>
    public static string Build(
        ServiceOrphanScanResult services,
        AppStateScanResult appStates,
        ReportFormat format,
        string summary,
        DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appStates);

        return format switch
        {
            ReportFormat.Csv => BuildCsv(services, appStates, summary),
            ReportFormat.Html => BuildHtml(services, appStates, summary, generatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知报告格式"),
        };
    }

    /// <summary>写文件并返回完整路径（UTF-8 带 BOM，Excel 中文不乱码）。</summary>
    public static string WriteTo(
        string directory,
        ServiceOrphanScanResult services,
        AppStateScanResult appStates,
        ReportFormat format,
        string summary,
        DateTime generatedAt)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SuggestFileName(format, generatedAt));
        File.WriteAllText(path, Build(services, appStates, format, summary, generatedAt),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string BuildCsv(ServiceOrphanScanResult s, AppStateScanResult a, string summary)
    {
        var builder = new StringBuilder();

        builder.AppendLine("类型,名称,详情,状态/建议,证据");

        foreach (var e in s.Entries)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                "服务孤儿",
                Csv(e.ServiceName),
                Csv($"{e.DisplayName}（{e.StartModeText}/{e.StateText}）"),
                Csv($"缺失: {e.ResolvedPath}"),
                Csv(string.Join(" | ", e.Evidence)),
            }));
        }

        foreach (var e in a.Entries)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                "状态不一致",
                Csv(e.DisplayName),
                Csv(e.Kind.ToString()),
                Csv(string.Join(" ", e.Suggestions)),
                Csv(string.Join(" | ", e.Evidence)),
            }));
        }

        builder.AppendLine();
        builder.AppendLine($"摘要,{Csv(summary)}");
        return builder.ToString();
    }

    private static string BuildHtml(
        ServiceOrphanScanResult s, AppStateScanResult a, string summary, DateTime generatedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html><head><meta charset=\"utf-8\"><title>RubbishCleaner 系统一致性报告</title></head><body>");
        builder.AppendLine($"<h1>系统一致性报告</h1>");
        builder.AppendLine($"<p>生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss}</p>");
        builder.AppendLine($"<p>{Escape(summary)}（<b>本报告只读，RubbishCleaner 不会自动清理任何项目</b>）</p>");

        builder.AppendLine($"<h2>服务 ImagePath 孤儿（{s.Entries.Count} 条 / 检查 {s.ScannedCount} 个服务）</h2>");
        if (s.Entries.Count == 0)
        {
            builder.AppendLine("<p>无</p>");
        }
        else
        {
            builder.AppendLine("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\">");
            builder.AppendLine("<tr><th>服务名</th><th>显示名</th><th>启动类型</th><th>状态</th><th>缺失路径</th><th>手动命令</th></tr>");
            foreach (var e in s.Entries)
            {
                builder.AppendLine($"<tr><td>{Escape(e.ServiceName)}</td><td>{Escape(e.DisplayName ?? "")}</td>" +
                    $"<td>{Escape(e.StartModeText)}</td><td>{Escape(e.StateText)}</td>" +
                    $"<td>{Escape(e.ResolvedPath)}</td><td><code>{Escape(e.ManualCommand)}</code></td></tr>");
            }
            builder.AppendLine("</table>");
        }

        builder.AppendLine($"<h2>文件与注册表状态不一致（{a.Entries.Count} 条 / 检查 {a.CheckedCount} 个软件）</h2>");
        if (a.Entries.Count == 0)
        {
            builder.AppendLine("<p>无</p>");
        }
        else
        {
            builder.AppendLine("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\">");
            builder.AppendLine("<tr><th>软件</th><th>产品码</th><th>类型</th><th>建议</th></tr>");
            foreach (var e in a.Entries)
            {
                builder.AppendLine($"<tr><td>{Escape(e.DisplayName)}</td><td>{Escape(e.ProductCode ?? "")}</td>" +
                    $"<td>{Escape(e.Kind.ToString())}</td><td>{Escape(string.Join("<br>", e.Suggestions))}</td></tr>");
            }
            builder.AppendLine("</table>");
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static string Escape(string? text)
        => (text ?? string.Empty)
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Csv(string? text)
    {
        var value = text ?? string.Empty;
        // 含逗号/引号/换行任一就要整体加引号，内部引号翻倍（RFC 4180）
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}