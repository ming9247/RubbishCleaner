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

    /// <summary>
    /// 驱动器相对路径不是盘根——<b>而且这个结论不能依赖运行环境</b>。
    ///
    /// 这是 v0.4.1 补的回归防护：原先 <c>IsVolumeRoot</c> 直接 <c>Path.GetFullPath("D:")</c> 再比盘根，
    /// 而 <c>GetFullPath</c> 会按进程隐藏环境变量 <c>=D:</c>（每盘当前目录）补全——
    /// 有该变量时得到 <c>D:\某目录</c>（判 false），没有时得到 <c>D:\</c>（判 true）。
    /// 于是同一份代码在开发者机器上绿、在干净环境里红（实测复现：清空 <c>=D:</c> 的子进程里
    /// <c>GetFullPath("D:")</c> 就是 <c>D:\</c>）。
    ///
    /// 现在 <c>IsVolumeRoot</c> 先看原始输入是否完全限定，驱动器相对形态一律返回 false，
    /// 与环境无关；而 <see cref="LeftoverSafetyGuard.Check"/> 传给它的仍是补全后的绝对路径，行为不变。
    /// </summary>
    [Theory]
    [InlineData("C:")]
    [InlineData("D:")]
    [InlineData("Z:")]
    public void IsVolumeRoot_DriveRelativePath_IsNotVolumeRoot(string path)
    {
        Assert.False(LeftoverSafetyGuard.IsVolumeRoot(path));
    }

    [Fact]
    public void Check_DriveRelativePath_IsRejectedBeforeResolution()
    {
        // 两道闸门都会拒绝，但先命中的必须是"不是绝对路径"——
        // 这样用户看到的原因才是真实原因，而不是"这是盘符根目录"这种误导性提示。
        var reason = LeftoverSafetyGuard.Check("D:", Self);

        Assert.Equal("不是绝对路径", reason);
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
