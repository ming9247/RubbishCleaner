# v0.4 注册表清理的真机验证脚本
# 用法：.\scripts\e2e-registry.ps1 [-Mode e2e|views|e2e32|all]
#
# 三种模式：
#   e2e    全链路：造孤儿条目 → 扫描 → 计划 → 备份 → 删键 → reg import 还原 → 现场清理
#          （不需要管理员：HKLM 写不了会自动退回 HKCU，判据与删除逻辑完全一致）
#   views  32 位视图 / 三上下文实测（只读）：条目统计、两视图差集、HKCU 重定向、备份路径
#   e2e32  HKLM 32 位视图的真实往返（备份 → 删键 → reg import 还原）——需要管理员
#          会自己请求提权（UAC）；拿不到权限时如实报“未执行”，不算失败
#
# 安全性：
#   * 只操作自己造的那一个 GUID 键（{DEADBEEF-...}），不碰任何真实软件条目；
#   * 无论成功失败，finally 都会删掉测试键；
#   * e2e32 会同时清 32 位与 64 位两个视图的测试键，防旧实现残留。

param(
    [ValidateSet('e2e', 'views', 'e2e32', 'all')]
    [string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\RubbishCleaner.RegE2E\RubbishCleaner.RegE2E.csproj'
$exe = Join-Path $root 'tools\RubbishCleaner.RegE2E\bin\Debug\net8.0-windows\RubbishCleaner.RegE2E.exe'

Push-Location $root
try {
    Write-Host '==> 构建探针' -ForegroundColor Cyan
    dotnet build $project -c Debug -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

    $failed = @()

    # ---------- views：只读实测 ----------
    if ($Mode -in @('views', 'all')) {
        Write-Host '' ; Write-Host '==> [1/3] 视图实测（只读）' -ForegroundColor Cyan
        & $exe views
        if ($LASTEXITCODE -ne 0) { $failed += "views (exit $LASTEXITCODE)" }
    }

    # ---------- e2e：全链路（可退化为 HKCU）----------
    if ($Mode -in @('e2e', 'all')) {
        Write-Host '' ; Write-Host '==> [2/3] 全链路 E2E' -ForegroundColor Cyan
        & $exe e2e
        if ($LASTEXITCODE -ne 0) { $failed += "e2e (exit $LASTEXITCODE)" }
    }

    # ---------- e2e32：HKLM 32 位真实往返（需要提权）----------
    if ($Mode -in @('e2e32', 'all')) {
        Write-Host '' ; Write-Host '==> [3/3] HKLM 32 位视图真实往返（需要管理员）' -ForegroundColor Cyan
        $logPath = Join-Path $env:TEMP 'rc-e2e32.log'
        Remove-Item $logPath -Force -ErrorAction SilentlyContinue

        try {
            # -Verb RunAs 与输出重定向不能同时用，所以探针自己写文件日志
            $proc = Start-Process -FilePath $exe -ArgumentList 'e2e32' -WorkingDirectory $root -Verb RunAs -PassThru
            $proc.WaitForExit(180000) | Out-Null
            $code = $proc.ExitCode
        }
        catch {
            Write-Host '  （未能提权，跳过这一项）' -ForegroundColor Yellow
            $code = 3
        }

        if (Test-Path $logPath) { Get-Content $logPath -Encoding UTF8 }

        switch ($code) {
            0 { }                       # 通过
            3 { Write-Host '  未执行：没有管理员权限（不算失败）' -ForegroundColor Yellow }
            default { $failed += "e2e32 (exit $code)" }
        }
    }

    Write-Host ''
    if ($failed.Count -eq 0) {
        Write-Host '==> 全部通过' -ForegroundColor Green
    }
    else {
        Write-Host ('==> 失败：' + ($failed -join '、')) -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}