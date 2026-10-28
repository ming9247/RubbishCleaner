# 生成开发/内部测试用的自签名代码签名证书
# 用法：
#   .\scripts\make-signing-cert.ps1                 # 没有证书就生成，有就复用
#   .\scripts\make-signing-cert.ps1 -Force          # 强制重新生成
#   .\scripts\make-signing-cert.ps1 -TrustLocal     # 同时把证书加入本机信任（仅本机有效）
#
# 说明：自签名证书只能证明「产物由持有私钥的人签名」，不能向 SmartScreen / 其他机器
# 证明发布者身份。正式对外发布必须换成 OV/EV 代码签名证书或 Azure Trusted Signing，
# 详见 docs/technical/技术设计文档.md §11.3。

param(
    [string]$Subject = 'CN=RubbishCleaner Dev, O=RubbishCleaner, C=CN',
    [string]$PfxPath = 'certs\rubbishcleaner-dev.pfx',
    [string]$CerPath = 'certs\rubbishcleaner-dev.cer',
    [string]$PasswordFile = 'certs\pfx-password.txt',
    [int]$Years = 2,
    [switch]$Force,
    [switch]$TrustLocal
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    foreach ($cmd in 'New-SelfSignedCertificate', 'Export-PfxCertificate', 'Export-Certificate') {
        if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
            throw "缺少 cmdlet $cmd（需要 PowerShell 的 PKI 模块）"
        }
    }

    # 1) 复用已有证书：按 Subject 在 CurrentUser\My 里找带 Code Signing EKU 的那张
    $existing = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'
        } | Sort-Object NotAfter -Descending)

    if ($existing.Count -gt 0 -and -not $Force) {
        $cert = $existing[0]
        Write-Host "==> 复用已有代码签名证书：$($cert.Subject)" -ForegroundColor Cyan
    }
    else {
        if ($existing.Count -gt 0 -and $Force) {
            Write-Host '==> -Force：删除旧的开发证书' -ForegroundColor Yellow
            foreach ($old in $existing) {
                Remove-Item -Path ('Cert:\CurrentUser\My\' + $old.Thumbprint) -Force -ErrorAction SilentlyContinue
            }
        }

        Write-Host "==> 生成自签名代码签名证书：$Subject（有效期 $Years 年）" -ForegroundColor Cyan
        $certParams = @{
            Type               = 'CodeSigningCert'
            Subject            = $Subject
            KeyUsage           = 'DigitalSignature'
            KeyExportPolicy    = 'Exportable'
            KeyAlgorithm       = 'RSA'
            KeyLength          = 3072
            HashAlgorithm      = 'SHA256'
            NotAfter           = (Get-Date).AddYears($Years)
            CertStoreLocation  = 'Cert:\CurrentUser\My'
            TextExtension      = @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        }
        $cert = New-SelfSignedCertificate @certParams
    }

    Write-Host ('     Thumbprint = ' + $cert.Thumbprint)
    Write-Host ('     有效期至   = ' + $cert.NotAfter.ToString('yyyy-MM-dd'))

    # 2) 导出 PFX（私钥）与 CER（公钥）
    $pfxFull = Join-Path $root $PfxPath
    $cerFull = Join-Path $root $CerPath
    $pwdFull = Join-Path $root $PasswordFile
    foreach ($dir in @((Split-Path -Parent $pfxFull), (Split-Path -Parent $cerFull), (Split-Path -Parent $pwdFull))) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }

    # 密码：优先复用已有密码文件（保证重复导出时一致），否则生成一个新的随机密码
    if (Test-Path $pwdFull) {
        $plain = (Get-Content -Path $pwdFull -Raw).Trim()
        Write-Host "==> 复用已有 PFX 密码文件：$PasswordFile" -ForegroundColor Cyan
    }
    else {
        $bytes = New-Object byte[] 24
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        $plain = [Convert]::ToBase64String($bytes)
        Set-Content -Path $pwdFull -Value $plain -NoNewline -Encoding ASCII
        Write-Host "==> 已生成 PFX 密码并写入：$PasswordFile（该目录已在 .gitignore 中）" -ForegroundColor Cyan
    }

    $secure = ConvertTo-SecureString -String $plain -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxFull -Password $secure -ChainOption EndEntityCertOnly | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerFull -Type CERT | Out-Null

    Write-Host "==> 已导出私钥：$PfxPath" -ForegroundColor Green
    Write-Host "==> 已导出公钥：$CerPath" -ForegroundColor Green

    # 3) 可选：加入本机信任（只影响这台机器，让 Get-AuthenticodeSignature 显示 Valid）
    if ($TrustLocal) {
        Write-Host '==> 把证书加入本机「受信任的根」与「受信任的发布者」（仅本机有效）' -ForegroundColor Yellow
        foreach ($store in @('Cert:\CurrentUser\Root', 'Cert:\CurrentUser\TrustedPublisher')) {
            try {
                Import-Certificate -FilePath $cerFull -CertStoreLocation $store | Out-Null
                Write-Host "     已导入 $store"
            }
            catch {
                Write-Host "     导入 $store 失败（可忽略）：$($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
    else {
        Write-Host '==> 未加入本机信任：签名会生成，但 Get-AuthenticodeSignature 报 UnknownError（自签名未受信）。' -ForegroundColor Yellow
        Write-Host '    如需本机验证签名状态为 Valid，重跑本脚本并加 -TrustLocal。' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host '下一步：' -ForegroundColor Cyan
    Write-Host '  .\scripts\sign.ps1 -Path dist\win-x64\RubbishCleaner.exe'
    Write-Host '  .\scripts\publish.ps1 -Sign'
}
finally {
    Pop-Location
}
