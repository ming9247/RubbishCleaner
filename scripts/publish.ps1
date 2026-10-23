# 单文件绿色版发布脚本
# 用法：.\scripts\publish.ps1 [-Runtime win-x64] [-Output dist\win-x64]

param(
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    if ([string]::IsNullOrWhiteSpace($Output)) {
        $Output = Join-Path 'dist' $Runtime
    }

    Write-Host "==> 发布单文件到 $Output" -ForegroundColor Cyan

    # 注意：绝不开启 PublishTrimmed —— WPF 与裁剪不兼容，会在运行期因反射/XAML 失效而崩溃（ADR-004）。
    dotnet publish 'src/RubbishCleaner.App/RubbishCleaner.App.csproj' `
        -c Release `
        -r $Runtime `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $Output

    if ($LASTEXITCODE -ne 0) {
        throw "发布失败（exit $LASTEXITCODE）"
    }

    $exe = Join-Path $Output 'RubbishCleaner.exe'
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host "==> 完成：$exe ($size MB)" -ForegroundColor Green
    }
    else {
        Write-Host '==> 未找到 RubbishCleaner.exe，请检查输出目录' -ForegroundColor Yellow
        Get-ChildItem $Output | Select-Object Name, Length
    }
}
finally {
    Pop-Location
}
