using RubbishCleaner.Common.Net;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;
using Xunit;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// TrustTableUpdater 单元测试(v0.7)。
/// </summary>
public class TrustTableUpdaterTests
{
    [Fact]
    public async Task CheckUpdate_WhenRemoteVersionHigher_ReturnsAvailable()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        // 本地版本1
        await File.WriteAllTextAsync(localPath, @"{""version"":1,""whitelist"":[],""blacklist"":[]}");

        var downloader = new MockHttpDownloader(@"{""version"":2,""whitelist"":[],""blacklist"":[]}");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.CheckUpdateAsync("http://fake.url");

        // Assert
        Assert.True(result.Available);
        Assert.Equal(1, result.CurrentVersion);
        Assert.Equal(2, result.RemoteVersion);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task CheckUpdate_WhenRemoteVersionSame_ReturnsNotAvailable()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        await File.WriteAllTextAsync(localPath, @"{""version"":5,""whitelist"":[],""blacklist"":[]}");

        var downloader = new MockHttpDownloader(@"{""version"":5,""whitelist"":[],""blacklist"":[]}");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.CheckUpdateAsync("http://fake.url");

        // Assert
        Assert.False(result.Available);
        Assert.Equal(5, result.CurrentVersion);
        Assert.Equal(5, result.RemoteVersion);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Update_WhenNewVersionAvailable_WritesToUserDirectory()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        await File.WriteAllTextAsync(localPath, @"{""version"":1,""whitelist"":[],""blacklist"":[]}");

        var newContent = @"{""version"":3,""updated"":""2026-10-25"",""whitelist"":[{""type"":""signer"",""value"":""TestSigner""}],""blacklist"":[]}";
        var downloader = new MockHttpDownloader(newContent);
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.OldVersion);
        Assert.Equal(3, result.NewVersion);
        Assert.Equal(localPath, result.FilePath);

        // 验证文件内容
        var savedContent = await File.ReadAllTextAsync(localPath);
        Assert.Contains(@"""version"":3", savedContent);
        Assert.Contains("TestSigner", savedContent);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Update_WhenAlreadyLatest_ReturnsFailure()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        await File.WriteAllTextAsync(localPath, @"{""version"":10,""whitelist"":[],""blacklist"":[]}");

        var downloader = new MockHttpDownloader(@"{""version"":10,""whitelist"":[],""blacklist"":[]}");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("已是最新版本", result.Message);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Update_WhenRemoteInvalidJson_ReturnsFailure()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "trust-table.json");

        await File.WriteAllTextAsync(localPath, @"{""version"":1,""whitelist"":[],""blacklist"":[]}");

        var downloader = new MockHttpDownloader("{ invalid json }");
        var updater = new TrustTableUpdater(downloader, localPath, null);

        // Act
        var result = await updater.UpdateAsync("http://fake.url");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("格式无效", result.Message);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    /// <summary>
    /// Mock HTTP下载器,用于测试。
    /// </summary>
    private sealed class MockHttpDownloader : HttpDownloader
    {
        private readonly string _responseContent;

        public MockHttpDownloader(string responseContent) : base(1, 0)
        {
            _responseContent = responseContent;
        }

        public override async Task<DownloadResult> DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<(long Downloaded, long? Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(destinationPath, _responseContent, cancellationToken);
            return new DownloadResult
            {
                Success = true,
                FilePath = destinationPath,
                FileSizeBytes = _responseContent.Length
            };
        }
    }
}
