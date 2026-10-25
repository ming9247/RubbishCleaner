using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 残留清理的安全闸门测试。
///
/// 这是整个 v0.3 里最不能出错的一段：它说"可以删"，用户就会真的少一整个目录。
/// 因此测试只断言<b>"拒绝"</b>的那一侧——每一条误判都要有对应用例兜住。
/// </summary>
public class LeftoverSafetyGuardTests
{
    private const string Self = @"D:\Tools\RubbishCleaner";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_EmptyPath_IsRejected(string? path)
    {
        Assert.NotNull(LeftoverSafetyGuard.Check(path, Self));
    }

    [Fact]
    public void Check_RelativePath_IsRejected()
    {
        Assert.NotNull(LeftoverSafetyGuard.Check(@"AppData\Local\Foo", Self));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public void Check_VolumeRoot_IsRejected(string path)
    {
        var reason = LeftoverSafetyGuard.Check(path, Self);

        Assert.NotNull(reason);
        Assert.Contains("根目录", reason);
    }

    [Fact]
    public void Check_ContainerDirectory_IsRejectedByName()
    {
        // C:\Program Files 只有 2 级，但<b>拒绝它的理由应该是"它是容器目录"</b>，
        // 而不是"层级浅"——后者会把 D:\CCswitch 这类合法的盘根残留一起误杀。
        var reason = LeftoverSafetyGuard.Check(@"C:\Program Files", Self);

        Assert.NotNull(reason);
        Assert.Contains("保留目录名", reason);
    }

    [Theory]
    [InlineData(@"D:\CCswitch")]
    [InlineData(@"E:\MyApp")]
    public void Check_DriveRootSubdirectory_IsAllowed(string path)
    {
        // 实测逼出来的规则：CC Switch 装在 D:\CCswitch（盘符 + 目录名，2 级），
        // 卸载后整个目录都是残留，必须允许清理。若要求 ≥ 3 级，
        // "装在盘根下"的软件会被全部判成不可清理。
        Assert.Null(LeftoverSafetyGuard.Check(path, Self));
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Users")]
    public void Check_ReservedNameAtDriveRoot_IsStillRejected(string path)
    {
        // 层级放宽了，但保留名判断仍然生效：C:\Windows / C:\Users 绝不能被删
        Assert.NotNull(LeftoverSafetyGuard.Check(path, Self));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Foo")]
    [InlineData(@"C:\Program Files (x86)\Vendor\Product")]
    [InlineData(@"C:\Users\taco\AppData\Local\SomeApp")]
    [InlineData(@"C:\ProgramData\Vendor")]
    public void Check_NormalLeftoverDirectory_IsAllowed(string path)
    {
        Assert.Null(LeftoverSafetyGuard.Check(path, Self));
    }

    [Theory]
    [InlineData(@"C:\Windows\SysWOW64\Foo")]
    [InlineData(@"C:\Windows\WinSxS\SomeComponent")]
    [InlineData(@"C:\Program Files\Common Files\Vendor")]
    [InlineData(@"C:\Program Files\WindowsApps\SomeApp")]
    [InlineData(@"C:\Program Files (x86)\InstallShield Installation Information\{GUID}")]
    public void Check_ProtectedSystemPath_IsRejected(string path)
    {
        var reason = LeftoverSafetyGuard.Check(path, Self);

        Assert.NotNull(reason);
        Assert.Contains("系统保护路径", reason);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft")]
    [InlineData(@"C:\Users\taco\AppData\Local\Temp")]
    [InlineData(@"C:\ProgramData\Package Cache")]
    public void Check_ReservedName_IsRejected(string path)
    {
        var reason = LeftoverSafetyGuard.Check(path, Self);

        Assert.NotNull(reason);
        Assert.Contains("保留目录名", reason);
    }

    [Fact]
    public void Check_SelfDirectory_IsRejected()
    {
        var reason = LeftoverSafetyGuard.Check(@"D:\Tools\RubbishCleaner", Self);

        Assert.NotNull(reason);
        Assert.Contains("本程序", reason);
    }

    [Fact]
    public void Check_DirectoryContainingSelf_IsRejected()
    {
        // 删掉 D:\Tools 会连带删掉正在运行的本程序
        var reason = LeftoverSafetyGuard.Check(@"D:\Tools", Self);

        Assert.NotNull(reason);
    }

    [Fact]
    public void Check_DirectoryInsideSelf_IsRejected()
    {
        var reason = LeftoverSafetyGuard.Check(@"D:\Tools\RubbishCleaner\backups", Self);

        Assert.NotNull(reason);
    }

    [Fact]
    public void IsVolumeRoot_RecognizesRootsAndSubdirectories()
    {
        Assert.True(LeftoverSafetyGuard.IsVolumeRoot(@"C:\"));
        Assert.True(LeftoverSafetyGuard.IsVolumeRoot(@"D:\"));
        Assert.False(LeftoverSafetyGuard.IsVolumeRoot(@"C:\Program Files"));

        // "D:" 是"驱动器相对路径"（等于 D: 盘的当前目录），不是盘根
        Assert.False(LeftoverSafetyGuard.IsVolumeRoot("D:"));
    }

    [Fact]
    public void Apply_AttachesBlockReasonWithoutDroppingItems()
    {
        // 关键语义：不能删的项依然要展示给用户，只是标记为不可删
        var items = new List<LeftoverItem>
        {
            Item(@"C:\Program Files\GoodLeftover"),
            Item(@"C:\Windows\SysWOW64\BadLeftover"),
        };

        var result = LeftoverSafetyGuard.Apply(items, Self);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0].BlockReason);
        Assert.True(result[0].CanDelete);
        Assert.NotNull(result[1].BlockReason);
        Assert.False(result[1].CanDelete);
    }

    [Fact]
    public void Apply_KeepsExistingBlockReason()
    {
        var items = new List<LeftoverItem>
        {
            Item(@"C:\Program Files\Foo") with { BlockReason = "扫描阶段已拒绝" },
        };

        var result = LeftoverSafetyGuard.Apply(items, Self);

        Assert.Equal("扫描阶段已拒绝", result[0].BlockReason);
    }

    private static LeftoverItem Item(string path) => new()
    {
        Id = path.ToLowerInvariant(),
        DirectoryPath = path,
        Kind = LeftoverKind.OrphanedUninstallerDirectory,
        Confidence = LeftoverConfidence.High,
    };
}
