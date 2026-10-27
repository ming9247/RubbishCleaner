using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>
/// 应用设置（v0.8）——持久化到 %ProgramData%\RubbishCleaner\settings.json。
///
/// <para><b>联网默认关闭</b>：v0.7 的核心约定。特征库在线更新是唯一联网功能，
/// 且必须用户显式勾选「启用联网」后才会发起任何网络请求；离线行为与 v0.6 完全一致。</para>
///
/// <para>用 System.Text.Json 手写序列化而不是引入配置库：字段只有三个，
/// 保持轻量。文件损坏时按默认值继续（工具类软件不能因为设置文件坏了就起不来）。</para>
/// </summary>
public sealed class AppSettings
{
    private static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RubbishCleaner",
        "settings.json");

    /// <summary>是否允许联网（特征库在线更新）。默认 false——不勾选就绝不联网。</summary>
    public bool EnableNetworking { get; set; }

    /// <summary>备份历史保留数量，超过的移入回收站。默认 30。</summary>
    public int KeepBackupCount { get; set; } = 30;

    /// <summary>
    /// 是否已经看过首次运行引导（v0.8）。默认 false。
    ///
    /// <para>只在用户<b>看完并关闭</b>引导后置 true，因此中途崩溃/断电不会把引导吞掉。
    /// 旧版本升级上来的 settings.json 没有这个字段，反序列化后为 false——
    /// 会再看一次引导，这是有意为之：v0.8 才第一次解释"风险分"是什么。</para>
    /// </summary>
    public bool HasSeenOnboarding { get; set; }

    /// <summary>
    /// 设置文件路径覆盖（不参与序列化）。供测试与特殊部署把设置重定向到别处；
    /// Save() 无参调用时优先用它。Load 显式传入路径时会把返回实例指回同一路径。
    /// </summary>
    [JsonIgnore]
    public string? SettingsPathOverride { get; set; }

    private string ResolvePath(string? path) => path ?? SettingsPathOverride ?? DefaultSettingsPath;

    /// <summary>读取设置；文件不存在或损坏时返回默认值。</summary>
    public static AppSettings Load(string? path = null)
    {
        var settingsPath = path ?? DefaultSettingsPath;
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new AppSettings { SettingsPathOverride = path };
            }

            var json = File.ReadAllText(settingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            loaded.SettingsPathOverride = path;
            return loaded;
        }
        catch (Exception)
        {
            // 设置文件损坏不阻断启动，按默认值继续。
            return new AppSettings { SettingsPathOverride = path };
        }
    }

    /// <summary>保存设置。失败时抛出（调用方决定如何提示）。</summary>
    public void Save(string? path = null)
    {
        var settingsPath = ResolvePath(path);
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }
}
