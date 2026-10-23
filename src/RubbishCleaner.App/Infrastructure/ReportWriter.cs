using System.IO;
using System.Text;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>报告格式。</summary>
public enum ReportFormat
{
    Csv,
    Html,
}

/// <summary>
/// 扫描报告生成器。
///
/// 从 ViewModel 中抽出，原因有二：
/// 1. 可测试：纯函数式生成字符串，不碰文件系统、不弹资源管理器；
/// 2. 可复用：后续 CLI 或定时导出可以直接调用。
/// 打开资源管理器这类副作用留在 ViewModel，保持本类无副作用。
/// </summary>
public static class ReportWriter
{
    /// <summary>生成报告文本。CSV 会带 BOM，保证 Excel 打开中文不乱码。</summary>
    public static string Build(
        IEnumerable<SoftwareProfile> profiles,
        ReportFormat format,
        string summary,
        string trustTableInfo,
        DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var list = profiles as IReadOnlyList<SoftwareProfile> ?? profiles.ToList();

        return format switch
        {
            ReportFormat.Csv => BuildCsv(list),
            ReportFormat.Html => BuildHtml(list, summary, trustTableInfo, generatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知报告格式"),
        };
    }

    /// <summary>建议的文件名（含时间戳，避免覆盖）。</summary>
    public static string SuggestFileName(ReportFormat format, DateTime timestamp)
        => $"RubbishCleaner-报告-{timestamp:yyyyMMdd-HHmmss}.{(format == ReportFormat.Csv ? "csv" : "html")}";

    /// <summary>写文件并返回完整路径。</summary>
    public static string WriteTo(
        string directory,
        IEnumerable<SoftwareProfile> profiles,
        ReportFormat format,
        string summary,
        string trustTableInfo,
        DateTime generatedAt)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SuggestFileName(format, generatedAt));
        var content = Build(profiles, format, summary, trustTableInfo, generatedAt);

        // UTF-8 带 BOM：Excel 才能正确识别中文
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string BuildCsv(IReadOnlyList<SoftwareProfile> profiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("风险等级,分数,名称,可执行文件路径,启动方式,发行商,特征库,运行状态,命中规则数,命中规则");

        foreach (var p in profiles)
        {
            var rules = string.Join(" | ", p.Score.Items
                .Where(i => i.Hit && !i.NotApplicable)
                .Select(i => i.Description));

            builder.AppendLine(string.Join(',', new[]
            {
                Csv(p.Score.Level.ToDisplayName()),
                p.Score.Total.ToString(),
                Csv(p.DisplayName),
                Csv(p.ExecutablePath ?? string.Empty),
                Csv(p.StartupSummary),
                Csv(p.Publisher ?? "（无签名）"),
                Csv(TrustText(p.Trust)),
                Csv(p.IsRunning ? "运行中" : "未运行"),
                Csv($"{p.Score.HitCount}/{p.Score.RuleCount}"),
                Csv(rules),
            }));
        }

        return builder.ToString();
    }

    private static string BuildHtml(
        IReadOnlyList<SoftwareProfile> profiles,
        string summary,
        string trustTableInfo,
        DateTime generatedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<title>RubbishCleaner 扫描报告</title>");
        builder.AppendLine("""
            <style>
            body{font-family:"Microsoft YaHei UI",Segoe UI,sans-serif;margin:24px;color:#1f2937;background:#f5f6f8}
            h1{font-size:20px;margin:0 0 4px}
            .meta{color:#6b7280;font-size:13px;margin-bottom:16px;line-height:1.7}
            table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}
            th,td{border:1px solid #e3e6ea;padding:8px 10px;text-align:left;vertical-align:top}
            th{background:#fafbfc;font-weight:600}
            tr:nth-child(even) td{background:#fcfcfd}
            .pill{display:inline-block;padding:1px 8px;border-radius:10px;color:#fff;font-size:12px;white-space:nowrap}
            .HighRisk{background:#d13a3a}.Suspicious{background:#d89b00}.Normal{background:#2e9e5b}
            .path{color:#6b7280;font-size:12px;word-break:break-all}
            .evidence{color:#6b7280;font-size:12px}
            </style>
            """);
        builder.AppendLine("</head><body>");
        builder.AppendLine("<h1>RubbishCleaner 扫描报告</h1>");
        builder.AppendLine(
            $"<div class=\"meta\">生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss}<br>" +
            $"统计：{Html(summary)}<br>特征库：{Html(trustTableInfo)}</div>");
        builder.AppendLine("<table><thead><tr><th>等级</th><th>分数</th><th>程序</th><th>启动方式</th><th>发行商</th><th>判定依据</th></tr></thead><tbody>");

        foreach (var p in profiles)
        {
            var evidences = string.Join("<br>", p.Evidences.Select(e => Html(e.Message)));
            builder.AppendLine(
                $"<tr><td><span class=\"pill {p.Score.Level}\">{Html(p.Score.Level.ToDisplayName())}</span></td>" +
                $"<td>{p.Score.Total}</td>" +
                $"<td><b>{Html(p.DisplayName)}</b><div class=\"path\">{Html(p.ExecutablePath ?? "（未知）")}</div></td>" +
                $"<td>{Html(p.StartupSummary)}</td>" +
                $"<td>{Html(p.Publisher ?? "（无签名）")}</td>" +
                $"<td class=\"evidence\">{evidences}</td></tr>");
        }

        builder.AppendLine("</tbody></table></body></html>");
        return builder.ToString();
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时加引号，内部引号翻倍。</summary>
    internal static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    internal static string Html(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string TrustText(TrustVerdict verdict) => verdict switch
    {
        TrustVerdict.Safe => "白名单",
        TrustVerdict.Malicious => "黑名单",
        _ => "未收录",
    };
}
