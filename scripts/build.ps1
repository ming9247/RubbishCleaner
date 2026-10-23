# 构建脚本
# 用法：.\scripts\build.ps1 [-Configuration Release] [-SkipTests]

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    Write-Host "==> 构建 $Configuration" -ForegroundColor Cyan
    dotnet build RubbishCleaner.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败（exit $LASTEXITCODE）"
    }

    if (-not $SkipTests) {
        Write-Host "==> 单元测试" -ForegroundColor Cyan
        dotnet test RubbishCleaner.sln -c $Configuration --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "测试失败（exit $LASTEXITCODE）"
        }
    }

    Write-Host "==> 完成" -ForegroundColor Green
}
finally {
    Pop-Location
}
