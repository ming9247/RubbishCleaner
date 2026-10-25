using System.Text;
using RubbishCleaner.Core.BackupHistory;
using Xunit;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// BackupHistoryStore 清理功能测试(v0.7)。
/// </summary>
public class BackupCleanupTests
{
    [Fact]
    public async Task CleanOldBackups_WhenFewerThanKeepCount_DoesNothing()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        // 创建3个备份文件（RegistryKeyBackupWriter 的形态：UTF-16LE+BOM、段头物理路径）
        for (int i = 1; i <= 3; i++)
        {
            var fileName = $"orphan-TestKey-{DateTime.Now.AddMinutes(-i):yyyyMMddHHmmss}.reg";
            var content = $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Test{i}]\r\n\"DisplayName\"=\"Test {i}\"\r\n";
            await File.WriteAllTextAsync(Path.Combine(tempDir, fileName), content, Encoding.Unicode);
        }

        var store = new BackupHistoryStore(tempDir);

        // Act
        var result = await store.CleanOldBackupsAsync(keepCount: 10);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalBackups);
        Assert.Equal(3, result.KeptBackups);
        Assert.Equal(0, result.RemovedBackups);
        Assert.Contains("无需清理", result.Message);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task CleanOldBackups_WithInvalidKeepCount_ReturnsFailure()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var store = new BackupHistoryStore(tempDir);

        // Act
        var result = await store.CleanOldBackupsAsync(keepCount: 0);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("至少为1", result.Message);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task CleanOldBackups_WhenDirectoryNotExists_ReturnsSuccess()
    {
        // Arrange
        var nonExistentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new BackupHistoryStore(nonExistentDir);

        // Act
        var result = await store.CleanOldBackupsAsync(keepCount: 10);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("不存在", result.Message);
    }
}
