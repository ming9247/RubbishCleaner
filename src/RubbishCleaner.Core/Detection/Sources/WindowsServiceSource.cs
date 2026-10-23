using Microsoft.Win32;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Common.Registry;

namespace RubbishCleaner.Core.Detection.Sources;

/// <summary>
/// Windows 服务数据源：只取"自动启动"的 Win32 服务（<c>Start=2</c>），
/// 排除内核驱动（<c>Type</c> 不含 0x10/0x20 位）与微软签名的系统服务，避免噪声。
/// </summary>
public sealed class WindowsServiceSource : IStartupSource
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>SERVICE_AUTO_START</summary>
    private const int StartAuto = 2;

    /// <summary>SERVICE_WIN32_OWN_PROCESS (0x10) | SERVICE_WIN32_SHARE_PROCESS (0x20)</summary>
    private const int Win32ServiceMask = 0x30;

    public StartupSourceKind Kind => StartupSourceKind.WindowsService;

    public bool RequiresElevation => false;

    public Task<SourceResult> CollectAsync(ScanContext context)
    {
        var entries = new List<StartupEntry>();
        var skipped = 0;
        string? firstError = null;

        var serviceNames = RegistryReader.EnumerateSubKeyNames(
            RegistryHive.LocalMachine, ServicesPath, RegistryView.Registry64, out var enumError);

        if (enumError is not null)
        {
            return Task.FromResult(SourceResult.Failed(Kind, enumError));
        }

        foreach (var serviceName in serviceNames)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var subPath = $@"{ServicesPath}\{serviceName}";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(subPath, writable: false);
                if (key is null)
                {
                    continue;
                }

                if (key.GetValue("Start") is not int start || start != StartAuto)
                {
                    continue;
                }

                if (key.GetValue("Type") is not int type || (type & Win32ServiceMask) == 0)
                {
                    continue; // 驱动类服务不参与"流氓软件"判定
                }

                var imagePath = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    continue;
                }

                var expanded = NormalizeImagePath(imagePath);
                var resolved = CommandLineResolver.Resolve(expanded);
                var signature = context.Signatures.Verify(resolved.ExecutablePath);

                // 微软签名的系统服务不进入列表（白名单兜底，减少用户噪声）。
                if (signature?.IsMicrosoft == true)
                {
                    continue;
                }

                var displayName = key.GetValue("DisplayName") as string;
                entries.Add(new StartupEntry
                {
                    Id = EntryId.Create(Kind, ServicesPath, serviceName),
                    Source = Kind,
                    Name = serviceName,
                    Location = $@"HKLM\{ServicesPath}\{serviceName}",
                    Scope = StartupScope.Machine,
                    RawCommand = expanded,
                    ExecutablePath = resolved.ExecutablePath,
                    Arguments = resolved.Arguments,
                    IsSigned = signature?.IsSigned == true,
                    IsMicrosoft = false,
                    Publisher = signature?.Publisher,
                    IsReadable = true,
                    ReadError = resolved.Note,
                });

                _ = displayName;
            }
            catch (System.Security.SecurityException)
            {
                // 个别服务键 ACL 限制读取，跳过但记录，最终以 Degraded 提示用户。
                skipped++;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        var degraded = skipped > 0 || firstError is not null;
        var error = degraded
            ? $"有 {skipped} 个服务键无法读取" + (firstError is null ? string.Empty : $"；{firstError}")
            : null;

        return Task.FromResult(new SourceResult(Kind, entries, degraded, error));
    }

    /// <summary>
    /// 规范化 ImagePath：
    /// <c>\SystemRoot\system32\a.exe</c> → <c>C:\Windows\system32\a.exe</c>；
    /// <c>\??\C:\x\a.exe</c> → <c>C:\x\a.exe</c>。
    /// </summary>
    internal static string NormalizeImagePath(string imagePath)
    {
        var path = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            path = Path.Combine(root, path[@"\SystemRoot\".Length..]);
        }
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
        }
        else if (path.StartsWith(@"%SystemRoot%\", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.ExpandEnvironmentVariables(path);
        }

        return path;
    }
}
