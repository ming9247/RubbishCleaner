using RubbishCleaner.Common.Net;
using RubbishCleaner.Core.Trust;
using Xunit;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// TrustTableUpdater 落盘与版本计算单元测试(v0.8)。
/// 覆盖两项实测缺陷的回归：
/// 1. 落盘改为"先写同目录临时文件、再原子替换"：成功时目标文件为新内容且不残留 .tmp，
///    失败时原文件保持不变、同样不残留临时文件(不会再出现被截断的 trust-table.json)；
/// 2. 当前版本取三层合并后的有效版本：用户表缺失的干净机器上不再算成 v0。
/// </summary>
public class TrustTableUpdaterAtomicWriteTests
{
    [Fact]
    public async Task Update_WhenSuccess_ReplacesTargetAtomicallyAndLeavesNoTempFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        await File.WriteAllTextAsync(localPath, @"{""version"":1,""whitelist"":[],""blacklist"":[]}");

        var newContent = @"{""version"":3,""updated"":""2026-10-25"",""whitelist"":[{""type"":""signer"",""value"":""AtomicSigner""}],""blacklist"":[]}";
        var downloader = new StubHttpDownloader(newContent);
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert：目标文件被替换为新内容
        Assert.True(result.Success);
        Assert.Equal(localPath, result.FilePath);
        var savedContent = await File.ReadAllTextAsync(localPath);
        Assert.Contains(@"""version"":3", savedContent);
        Assert.Contains("AtomicSigner", savedContent);

        // Assert：原子替换完成后目录里只剩目标文件，没有 .tmp 残留
        Assert.False(File.Exists(localPath + ".tmp"));
        var files = Directory.GetFiles(tempDir);
        Assert.Single(files);
        Assert.Equal(localPath, files[0]);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Update_WhenRemoteInvalidJson_KeepsOriginalFileAndLeavesNoTempFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        var oldContent = @"{""version"":1,""whitelist"":[],""blacklist"":[]}";
        await File.WriteAllTextAsync(localPath, oldContent);

        var downloader = new StubHttpDownloader("{ invalid json }");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert：校验失败时不落盘，原文件内容保持不变
        Assert.False(result.Success);
        Assert.Contains("格式无效", result.Message);
        Assert.Equal(oldContent, await File.ReadAllTextAsync(localPath));

        // Assert：失败路径也不残留临时文件
        Assert.False(File.Exists(localPath + ".tmp"));
        Assert.Single(Directory.GetFiles(tempDir));

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task CheckUpdate_WhenUserTableMissing_CurrentVersionIsBuiltInVersion()
    {
        // Arrange：用户表不存在(干净机器)，有效版本应为内置表 v1
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        var downloader = new StubHttpDownloader(@"{""version"":2,""whitelist"":[],""blacklist"":[]}");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.CheckUpdateAsync("http://fake.url");

        // Assert：当前版本取三层合并结果(内置表 v1)，不再是 0
        Assert.True(result.Available);
        Assert.Equal(1, result.CurrentVersion);
        Assert.Equal(2, result.RemoteVersion);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Update_WhenUserTableMissing_OldVersionIsMergedVersion()
    {
        // Arrange：用户表不存在，更新前版本按合并结果算作 1
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        var downloader = new StubHttpDownloader(@"{""version"":2,""whitelist"":[],""blacklist"":[]}");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert：更新成功，旧版本为合并后的 v1，用户表被创建
        Assert.True(result.Success);
        Assert.Equal(1, result.OldVersion);
        Assert.Equal(2, result.NewVersion);
        Assert.True(File.Exists(localPath));
        Assert.Contains(@"""version"":2", await File.ReadAllTextAsync(localPath));

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    /// <summary>
    /// 桩下载器：把预设内容直接写到目标路径，用于隔离网络。
    /// </summary>
    private sealed class StubHttpDownloader : HttpDownloader
    {
        private readonly string _responseContent;

        public StubHttpDownloader(string responseContent) : base(1, 0)
        {
            _responseContent = responseContent;
        }

        public override async Task<DownloadResult> DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<(long Downloaded, long? Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(destinationPath, _responseContent, cancellationToken);
            progress?.Report((_responseContent.Length, (long?)_responseContent.Length));

            return new DownloadResult
            {
                Success = true,
                FilePath = destinationPath,
                FileSizeBytes = _responseContent.Length
            };
        }
    }
}
