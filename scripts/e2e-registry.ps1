# v0.4 注册表清理的真机 E2E 脚本
# 用法：.\scripts\e2e-registry.ps1
#
# 会造一个假的「孤儿 Uninstall 条目」（GUID 不在 Windows Installer 数据库里），
# 然后跑完整链路：扫描 → 计划 → 备份 → 删键 → reg import 还原 → 现场清理。
#
# 安全性：
#   * 只操作自己造的那一个 GUID 键，不碰任何真实软件条目；
#   * 无论成功失败，finally 都会删掉测试键；
#   * 有管理员权限时用 HKLM，否则自动退回 HKCU（判据与删除逻辑完全一致）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\RubbishCleaner.RegE2E\RubbishCleaner.RegE2E.csproj'

Push-Location $root
try {
    Write-Host '==> 构建 E2E 探针' -ForegroundColor Cyan
    dotnet build $project -c Debug -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

    Write-Host '==> 执行 E2E' -ForegroundColor Cyan
    dotnet run --project $project -c Debug --no-build
    if ($LASTEXITCODE -ne 0) { throw "E2E 未通过（exit $LASTEXITCODE）" }

    Write-Host '==> E2E 全部通过' -ForegroundColor Green
}
finally {
    Pop-Location
}
