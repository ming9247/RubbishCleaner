# 对发布产物做 Authenticode 代码签名
# 用法：
#   .\scripts\sign.ps1 -Path dist\win-x64\RubbishCleaner.exe
#   .\scripts\sign.ps1 -Path <exe> -Thumbprint <证书指纹>       # 用证书库里的证书
#   .\scripts\sign.ps1 -Path <exe> -PfxPath certs\x.pfx -PfxPasswordFile certs\pfx-password.txt
#   .\scripts\sign.ps1 -Path <exe> -NoTimestamp                 # 离线环境不打时间戳
#
# 证书来源优先级：-PfxPath > -Thumbprint > certs\rubbishcleaner-dev.pfx（开发自签名）。
# 找不到证书时以 exit code 2 结束，调用方可据此判断「未签名」而不是「签名失败」。
#
# 说明：时间戳让签名在证书过期后仍然有效，需要联网（默认 DigiCert RFC3161）。
# 自签名证书打时间戳通常会被服务器拒绝，脚本会自动降级为「无时间戳签名」并提示。

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Thumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPasswordFile = 'certs\pfx-password.txt',
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

# 找不到证书的专用退出码：调用方（publish.ps1）据此区分「没证书」与「签名出错」
$ExitNoCertificate = 2

try {
    if (-not (Test-Path $Path)) {
        throw "待签名文件不存在：$Path"
    }
    $item = Get-Item $Path
    Write-Host "==> 准备签名：$($item.FullName)（$([math]::Round($item.Length / 1MB, 1)) MB）" -ForegroundColor Cyan

    # 1) 取证书
    $cert = $null
    $pfxFull = $null
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $pfxFull = if (Test-Path $PfxPath) { (Resolve-Path $PfxPath).Path } else { Join-Path $root $PfxPath }
    }
    elseif ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        $defaultPfx = Join-Path $root 'certs\rubbishcleaner-dev.pfx'
        if (Test-Path $defaultPfx) { $pfxFull = $defaultPfx }
    }

    if ($pfxFull) {
        if (-not (Test-Path $pfxFull)) {
            Write-Host "==> 未找到 PFX：$pfxFull" -ForegroundColor Yellow
            exit $ExitNoCertificate
        }
        $pwdFull = if (Test-Path $PfxPasswordFile) { $PfxPasswordFile } else { Join-Path $root $PfxPasswordFile }
        if (-not (Test-Path $pwdFull)) {
            throw "找到 PFX 但缺少密码文件：$PfxPasswordFile（可用 -PfxPasswordFile 指定）"
        }
        $plain = (Get-Content -Path $pwdFull -Raw).Trim()
        # PowerShell 5.1 的 Get-PfxCertificate 不支持 -Password，这里直接用 .NET API 加载私钥
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            $pfxFull, $plain, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
        if (-not $cert.HasPrivateKey) {
            throw "PFX 已加载但没有私钥，无法签名：$pfxFull"
        }
        Write-Host "    证书来源：PFX $pfxFull"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $cert = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint })[0]
        if (-not $cert) {
            Write-Host "==> 证书库中找不到指纹 $Thumbprint" -ForegroundColor Yellow
            exit $ExitNoCertificate
        }
        Write-Host "    证书来源：证书库 $Thumbprint"
    }
    else {
        Write-Host '==> 没有可用代码签名证书（既无 PFX 也未指定 -Thumbprint）' -ForegroundColor Yellow
        Write-Host '    先生成开发用自签名证书：.\scripts\make-signing-cert.ps1' -ForegroundColor Yellow
        exit $ExitNoCertificate
    }

    Write-Host "    Subject = $($cert.Subject)"
    Write-Host "    有效期至 = $($cert.NotAfter.ToString('yyyy-MM-dd'))"

    # 2) 签名（先尝试带时间戳，失败则降级）
    $signed = $false
    if (-not $NoTimestamp) {
        try {
            Set-AuthenticodeSignature -FilePath $item.FullName -Certificate $cert `
                -HashAlgorithm SHA256 -TimestampServer $TimestampServer | Out-Null
            $signed = $true
            Write-Host "==> 已签名（含时间戳 $TimestampServer）" -ForegroundColor Green
        }
        catch {
            Write-Host "==> 时间戳失败，降级为无时间戳签名：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if (-not $signed) {
        Set-AuthenticodeSignature -FilePath $item.FullName -Certificate $cert -HashAlgorithm SHA256 | Out-Null
        Write-Host '==> 已签名（无时间戳）' -ForegroundColor Green
    }

    # 3) 回读校验
    $sig = Get-AuthenticodeSignature -FilePath $item.FullName
    Write-Host "    签名状态 = $($sig.Status)"
    Write-Host "    签名者   = $($sig.SignerCertificate.Subject)"
    if ($sig.Status -ne 'Valid') {
        Write-Host "    提示：Status 不是 Valid 属正常——自签名证书未被本机信任。" -ForegroundColor Yellow
        Write-Host "    想让本机显示 Valid：.\scripts\make-signing-cert.ps1 -TrustLocal" -ForegroundColor Yellow
    }
    if (-not $sig.SignerCertificate) {
        throw '签名后读不到签名者证书，签名可能未生效'
    }

    # 4) 记录签名信息，便于发布页展示
    $info = [ordered]@{
        file       = $item.FullName
        sizeBytes  = $item.Length
        subject    = $sig.SignerCertificate.Subject
        thumbprint = $sig.SignerCertificate.Thumbprint
        notAfter   = $sig.SignerCertificate.NotAfter.ToString('yyyy-MM-dd')
        status     = [string]$sig.Status
        timestamp  = (-not $NoTimestamp) -and $signed
        signedAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    }
    $infoPath = Join-Path $item.DirectoryName 'signature-info.json'
    ($info | ConvertTo-Json -Depth 4) | Set-Content -Path $infoPath -Encoding UTF8
    Write-Host "==> 签名信息已写入：$infoPath" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "==> 签名失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
