# 测试脚本
# 用法：.\scripts\test.ps1 [-Configuration Debug] [-Coverage]

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Coverage
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $testArgs = @(
        'test', 'RubbishCleaner.sln',
        '-c', $Configuration,
        '--logger', 'console;verbosity=normal'
    )

    if ($Coverage) {
        $testArgs += @('--collect:"XPlat Code Coverage"')
        Write-Host '==> 已启用覆盖率采集（TestResults/）' -ForegroundColor Yellow
    }

    dotnet @testArgs
    $exit = $LASTEXITCODE

    if ($exit -eq 0) {
        Write-Host '==> 全部测试通过' -ForegroundColor Green
    }
    else {
        Write-Host "==> 测试失败（exit $exit）" -ForegroundColor Red
    }

    exit $exit
}
finally {
    Pop-Location
}
