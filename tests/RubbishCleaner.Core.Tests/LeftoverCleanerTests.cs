using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 残留清理器测试。
///
/// 全部用注入的假删除实现，<b>绝不真删磁盘</b>——但断言的核心是
/// "不该删的绝对不进计划"，以及"移入回收站失败时绝不谎报成功"。
/// </summary>
public class LeftoverCleanerTests
{
    private static LeftoverItem Item(
        string path,
        LeftoverConfidence confidence = LeftoverConfidence.High,
        string? blockReason = null,
        LeftoverKind kind = LeftoverKind.OrphanedUninstallerDirectory,
        long bytes = 1024)
        => new()
        {
            Id = path.ToLowerInvariant(),
            DirectoryPath = path,
            Kind = kind,
            Confidence = confidence,
            TotalBytes = bytes,
            FileCount = 3,
            BlockReason = blockReason,
            Evidence = ["测试证据"],
        };

    private static LeftoverCleaner Cleaner(
        List<string>? deleted = null,
        Func<string, LeftoverDeleteOutcome>? delete = null,
        Func<string, bool>? exists = null)
        => new(
            delete ?? (path =>
            {
                deleted?.Add(path);
                return LeftoverDeleteOutcome.Ok();
            }),
            exists ?? (_ => true));

    [Fact]
    public void BuildPlan_IncludesHighConfidenceUnblockedItem()
    {
        var cleaner = Cleaner();

        var plan = cleaner.BuildPlan([Item(@"C:\Program Files\Gone")]);

        Assert.Equal(1, plan.Count);
        Assert.Equal(@"C:\Program Files\Gone", plan.Targets[0].DirectoryPath);
    }

    [Fact]
    public void BuildPlan_RejectsMediumAndLowConfidence()
    {
        var cleaner = Cleaner();

        var plan = cleaner.BuildPlan(
        [
            Item(@"C:\Program Files\Maybe", LeftoverConfidence.Medium),
            Item(@"C:\Program Files\Guess", LeftoverConfidence.Low),
        ]);

        Assert.True(plan.IsEmpty);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.All(plan.Skipped, s => Assert.Contains("置信度不足", s.Reason));
    }

    [Fact]
    public void BuildPlan_RejectsBlockedItemAndKeepsReason()
    {
        var cleaner = Cleaner();

        var plan = cleaner.BuildPlan([Item(@"C:\Windows\SysWOW64\X", blockReason: "位于系统保护路径")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("系统保护路径", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_RejectsMissingDirectory()
    {
        var cleaner = Cleaner(exists: _ => false);

        var plan = cleaner.BuildPlan([Item(@"C:\Program Files\AlreadyGone")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("已不存在", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_ComputesTotalBytes()
    {
        var cleaner = Cleaner();

        var plan = cleaner.BuildPlan(
        [
            Item(@"C:\Program Files\A", bytes: 1000),
            Item(@"C:\Program Files\B", bytes: 2000),
        ]);

        Assert.Equal(3000, plan.TotalBytes);
    }

    [Fact]
    public void Execute_DeletesEveryTargetAndReportsSuccess()
    {
        var deleted = new List<string>();
        var cleaner = Cleaner(deleted);
        var plan = cleaner.BuildPlan(
        [
            Item(@"C:\Program Files\A"),
            Item(@"C:\Program Files\B"),
        ]);

        var results = cleaner.Execute(plan);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal([@"C:\Program Files\A", @"C:\Program Files\B"], deleted);
    }

    [Fact]
    public void Execute_FailureIsNotReportedAsSuccess()
    {
        // 回收站拒绝（例如文件被占用）时，必须如实报告失败，不能吞掉
        var cleaner = Cleaner(delete: _ => LeftoverDeleteOutcome.Fail("文件被占用"));
        var plan = cleaner.BuildPlan([Item(@"C:\Program Files\Busy")]);

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("占用", results[0].Message);
    }

    [Fact]
    public void Execute_RevalidatesExistenceBeforeDeleting()
    {
        // 扫描到确认之间目录被删掉了：跳过而不是硬删（也不会报成失败以外的怪状态）
        var exists = true;
        var deleted = new List<string>();
        var cleaner = new LeftoverCleaner(
            path =>
            {
                deleted.Add(path);
                return LeftoverDeleteOutcome.Ok();
            },
            _ => exists);

        var plan = cleaner.BuildPlan([Item(@"C:\Program Files\Race")]);
        exists = false;   // 点确认的瞬间目录已经没了

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Empty(deleted);
        Assert.Contains("已不存在", results[0].Message);
    }

    [Fact]
    public void Execute_OneFailureDoesNotStopTheRest()
    {
        var calls = 0;
        var cleaner = new LeftoverCleaner(
            _ =>
            {
                calls++;
                return calls == 1 ? LeftoverDeleteOutcome.Fail("第一个失败") : LeftoverDeleteOutcome.Ok();
            },
            _ => true);

        var plan = cleaner.BuildPlan(
        [
            Item(@"C:\Program Files\A"),
            Item(@"C:\Program Files\B"),
        ]);

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Execute_EmptyPlan_DoesNothing()
    {
        var deleted = new List<string>();
        var cleaner = Cleaner(deleted);

        var results = cleaner.Execute(new LeftoverCleanupPlan());

        Assert.Empty(results);
        Assert.Empty(deleted);
    }
}
