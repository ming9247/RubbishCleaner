using Microsoft.Win32;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Common.Registry;

namespace RubbishCleaner.Core.Detection.Sources;

/// <summary>
/// 注册表 Run / RunOnce 数据源，覆盖 HKCU、HKLM 与 WOW6432Node 兼容层。
/// </summary>
public sealed class RegistryRunSource : IStartupSource
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    public StartupSourceKind Kind { get; }

    public bool RequiresElevation => false;

    private readonly RegistryHive _hive;
    private readonly RegistryView _view;
    private readonly StartupScope _scope;

    public RegistryRunSource(StartupSourceKind kind)
    {
        Kind = kind;
        (_hive, _view, _scope) = kind switch
        {
            StartupSourceKind.RegistryRunHkcu => (RegistryHive.CurrentUser, RegistryView.Default, StartupScope.User),
            StartupSourceKind.RegistryRunHklm => (RegistryHive.LocalMachine, RegistryView.Registry64, StartupScope.Machine),
            StartupSourceKind.RegistryRunWow64 => (RegistryHive.LocalMachine, RegistryView.Registry32, StartupScope.Machine),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "非注册表 Run 类型"),
        };
    }

    public Task<SourceResult> CollectAsync(ScanContext context)
    {
        var entries = new List<StartupEntry>();
        string? firstError = null;

        foreach (var (path, label) in new[] { (RunPath, "Run"), (RunOncePath, "RunOnce") })
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var values = RegistryReader.ReadStringValues(_hive, path, _view, out var error);
            if (error is not null)
            {
                firstError ??= $"{label}: {error}";
            }

            foreach (var value in values)
            {
                var resolved = CommandLineResolver.Resolve(value.Value);
                var signature = context.Signatures.Verify(resolved.ExecutablePath);

                entries.Add(new StartupEntry
                {
                    Id = EntryId.Create(Kind, $"{path}\\{value.Name}", value.Name),
                    Source = Kind,
                    Name = value.Name,
                    Location = $@"{HiveName(_hive)}\{path}",
                    Scope = _scope,
                    RawCommand = value.Value,
                    ExecutablePath = resolved.ExecutablePath,
                    Arguments = resolved.Arguments,
                    IsSigned = signature?.IsSigned == true,
                    IsMicrosoft = signature?.IsMicrosoft == true,
                    Publisher = signature?.Publisher,
                    IsReadable = true,
                    ReadError = resolved.Note,

                    // 精确坐标：后续"清理残留启动项"靠它定位，而不是解析 Location 字符串。
                    RegistryAnchor = new RegistryAnchor(_hive, _view, path, value.Name),
                });
            }
        }

        return Task.FromResult(entries.Count == 0 && firstError is not null
            ? SourceResult.Failed(Kind, firstError)
            : new SourceResult(Kind, entries, firstError is not null, firstError));
    }

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        _ => hive.ToString(),
    };
}
