using RubbishCleaner.Core.Detection;

namespace RubbishCleaner.Core.Tests;

public class CommandLineResolverTests
{
    [Fact]
    public void Resolve_QuotedPathWithArguments_ReturnsPathAndArguments()
    {
        var result = CommandLineResolver.Resolve("\"C:\\Program Files\\App\\app.exe\" --silent /startup");

        Assert.Equal("C:\\Program Files\\App\\app.exe", result.ExecutablePath);
        Assert.Equal("--silent /startup", result.Arguments);
    }

    [Fact]
    public void Resolve_UnquotedPathWithoutSpaces_ReturnsPath()
    {
        var result = CommandLineResolver.Resolve("C:\\Tools\\a.exe -x");

        Assert.Equal("C:\\Tools\\a.exe", result.ExecutablePath);
        Assert.Equal("-x", result.Arguments);
    }

    [Fact]
    public void Resolve_EmptyCommand_ReturnsNullPath()
    {
        var result = CommandLineResolver.Resolve("   ");

        Assert.Null(result.ExecutablePath);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Resolve_NullCommand_ReturnsNullPath()
    {
        Assert.Null(CommandLineResolver.Resolve(null).ExecutablePath);
    }

    [Fact]
    public void Resolve_ShellProtocol_ReturnsNullPath()
    {
        var result = CommandLineResolver.Resolve("explorer.exe shell:startup");

        Assert.Null(result.ExecutablePath);
        Assert.Contains("shell", result.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Rundll32_AttributesToDll()
    {
        var result = CommandLineResolver.Resolve("rundll32.exe \"C:\\App\\hook.dll\",EntryPoint");

        Assert.Equal("C:\\App\\hook.dll", result.ExecutablePath);
    }

    [Fact]
    public void Resolve_Svchost_KeepsHostProcess()
    {
        var result = CommandLineResolver.Resolve("C:\\Windows\\System32\\svchost.exe -k netsvcs");

        Assert.Equal("C:\\Windows\\System32\\svchost.exe", result.ExecutablePath);
        Assert.Equal("系统宿主进程", result.Note);
    }

    [Fact]
    public void Resolve_CmdWithSlashC_ExtractsInnerCommand()
    {
        var result = CommandLineResolver.Resolve("cmd.exe /c \"C:\\App\\run.exe\" -quiet");

        Assert.Equal("C:\\App\\run.exe", result.ExecutablePath);
    }

    [Fact]
    public void Resolve_RelativePath_IsReturnedAsRelative()
    {
        var result = CommandLineResolver.Resolve("app.exe -start");

        Assert.Equal("app.exe", result.ExecutablePath);
        Assert.Equal("相对路径", result.Note);
    }

    [Fact]
    public void Resolve_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("RC_TEST_DIR", "C:\\RcTest");
        try
        {
            var result = CommandLineResolver.Resolve("%RC_TEST_DIR%\\a.exe");

            Assert.Equal("C:\\RcTest\\a.exe", result.ExecutablePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RC_TEST_DIR", null);
        }
    }

    [Fact]
    public void GuessExecutableFromUnquoted_ExistingFileWithSpaces_ReturnsLongestMatch()
    {
        // 构造一个真实存在的临时文件，验证"从最长前缀向前回溯"的逻辑
        var dir = Path.Combine(Path.GetTempPath(), "rc test dir " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "my app.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var guessed = CommandLineResolver.GuessExecutableFromUnquoted($"{exe} -arg");

            Assert.Equal(exe, guessed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GuessExecutableFromUnquoted_NoExistingFile_ReturnsFirstToken()
    {
        var guessed = CommandLineResolver.GuessExecutableFromUnquoted("ghost.exe --flag");

        Assert.Equal("ghost.exe", guessed);
    }
}
