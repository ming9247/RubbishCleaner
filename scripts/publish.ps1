# 单文件绿色版发布脚本
# 用法：
#   .\scripts\publish.ps1 [-Runtime win-x64] [-Output dist\win-x64]
#   .\scripts\publish.ps1 -Sign                 # 发布后做 Authenticode 签名（无证书时自动跳过）
#
# 签名说明（见 docs/technical/技术设计文档.md §11.3）：
#   - 证书来源：certs\rubbishcleaner-dev.pfx（开发自签名，先用 make-signing-cert.ps1 生成）
#   - 也可用 -Thumbprint 指定证书库里的 OV/EV 代码签名证书
#   - 找不到证书不算失败：脚本会打印「未签名」并继续，退出码仍为 0

param(
    [string]$Runtime = 'win-x64',
    [string]$Output = '',
    [switch]$Sign,
    [string]$Thumbprint = '',
    [switch]$NoTimestamp
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

    # ---------- 代码签名 ----------
    if ($Sign) {
        if (-not (Test-Path $exe)) {
            throw '开启了 -Sign 但没找到 RubbishCleaner.exe'
        }
        $signArgs = @{ Path = $exe }
        if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) { $signArgs['Thumbprint'] = $Thumbprint }
        if ($NoTimestamp) { $signArgs['NoTimestamp'] = $true }

        Write-Host '==> 开始签名' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'sign.ps1') @signArgs
        $signExit = $LASTEXITCODE
        if ($signExit -eq 2) {
            Write-Host '==> 未签名：没有可用代码签名证书（发布产物仍可用，但 SmartScreen 会提示）' -ForegroundColor Yellow
            Write-Host '    生成开发用自签名证书：.\scripts\make-signing-cert.ps1' -ForegroundColor Yellow
        }
        elseif ($signExit -ne 0) {
            throw "签名失败（exit $signExit）"
        }
    }
    else {
        Write-Host '==> 已跳过签名（如需签名加 -Sign）' -ForegroundColor DarkGray
    }
}
finally {
    Pop-Location
}
