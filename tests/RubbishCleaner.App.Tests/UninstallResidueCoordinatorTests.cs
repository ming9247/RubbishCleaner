using System.IO;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 卸载联动的协调者（v0.9.6）。
///
/// 关键约定：<b>没有 InstallLocation 的软件也要留快照</b>。
/// 很多按用户安装的软件不写 InstallLocation，以前这里直接返回 false，
/// 整条"卸载后定向扫描"被跳过——%AppData% 里的残留就永远扫不到。
/// </summary>
public class UninstallResidueCoordinatorTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-coord-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static InstalledApp App(string name, string? installLocation) => new()
    {
        Id = name,
        DisplayName = name,
        RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
        InstallLocation = installLocation,
        UninstallCommand = @"C:\Fake\unins000.exe",
    };

    [Fact]
    public void CaptureBefore_WithoutInstallLocation_StillRecordsSnapshot()
    {
        var store = TempDir();
        try
        {
            var coordinator = new UninstallResidueCoordinator(
                snapshotDirectory: store,
                installedAppsProvider: () => []);

            var ok = coordinator.CaptureBefore(App("无安装路径的软件", null));

            Assert.True(ok);
            Assert.Single(Directory.GetFiles(store, "*.json"));
        }
        finally
        {
            Cleanup(store);
        }
    }

    [Fact]
    public void CaptureBefore_WithInstallLocation_RecordsFileList()
    {
        var store = TempDir();
        var install = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(install, "app.exe"), "x");
            var coordinator = new UninstallResidueCoordinator(
                snapshotDirectory: store,
                installedAppsProvider: () => []);

            Assert.True(coordinator.CaptureBefore(App("有安装路径的软件", install)));

            var json = File.ReadAllText(Directory.GetFiles(store, "*.json").Single());
            Assert.Contains("app.exe", json);
        }
        finally
        {
            Cleanup(store);
            Cleanup(install);
        }
    }
}
