using RubbishCleaner.Common.Win32;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Detection.Sources;

/// <summary>
/// 启动文件夹数据源（用户 / 公共）。文件夹内绝大多数是 .lnk 快捷方式，需解析出真实目标。
/// </summary>
public sealed class StartupFolderSource : IStartupSource
{
    public StartupSourceKind Kind { get; }

    public bool RequiresElevation => false;

    private readonly string _folder;

    public StartupFolderSource(StartupSourceKind kind)
    {
        Kind = kind;
        (_folder, Scope) = kind switch
        {
            StartupSourceKind.StartupFolderUser => (
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup"),
                StartupScope.User),
            StartupSourceKind.StartupFolderMachine => (
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup"),
                StartupScope.Machine),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "非启动文件夹类型"),
        };
    }

    public StartupScope Scope { get; }

    public Task<SourceResult> CollectAsync(ScanContext context)
    {
        var entries = new List<StartupEntry>();

        if (!Directory.Exists(_folder))
        {
            return Task.FromResult(SourceResult.Empty(Kind));
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(file);
                var isLink = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

                string? target = null;
                string? arguments = null;
                string? readError = null;

                if (isLink)
                {
                    var link = ShellLinkReader.Read(file);
                    target = link.TargetPath;
                    arguments = link.Arguments;
                    readError = link.Error;
                }
                else
                {
                    // 直接丢进启动文件夹的 exe / bat
                    target = file;
                }

                var signature = context.Signatures.Verify(target);

                entries.Add(new StartupEntry
                {
                    Id = EntryId.Create(Kind, _folder, name),
                    Source = Kind,
                    Name = name,
                    Location = _folder,
                    Scope = Scope,
                    RawCommand = isLink ? target : file,
                    ExecutablePath = string.IsNullOrWhiteSpace(target) ? null : target,
                    Arguments = arguments,
                    IsSigned = signature?.IsSigned == true,
                    IsMicrosoft = signature?.IsMicrosoft == true,
                    Publisher = signature?.Publisher,
                    IsReadable = string.IsNullOrWhiteSpace(target) == false,
                    ReadError = readError,
                });
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(SourceResult.Failed(Kind, ex.Message));
        }

        return Task.FromResult(new SourceResult(Kind, entries));
    }
}
