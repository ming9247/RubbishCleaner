# 环境探测脚本（只读）
# 用途：生成 docs/environment 中环境清单所需的原始数据。
# 用法：.\scripts\probe-env.ps1 [-Json]

param(
    [switch]$Json
)

$ErrorActionPreference = 'Continue'

function Get-ToolVersion {
    param([string]$Command, [string]$Arguments = '--version')
    try {
        $output = & $Command $Arguments 2>&1 | Select-Object -First 1
        return ($output -join ' ').Trim()
    }
    catch {
        return '未安装'
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
$system = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue

$report = [ordered]@{
    collectedAt       = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    osCaption         = if ($os) { $os.Caption } else { 'unknown' }
    osVersion         = if ($os) { $os.Version } else { 'unknown' }
    architecture      = $env:PROCESSOR_ARCHITECTURE
    isElevated        = $isAdmin
    user              = "$env:USERDOMAIN\$env:USERNAME"
    machine           = $env:COMPUTERNAME
    powershell        = $PSVersionTable.PSVersion.ToString()
    dotnetSdks        = @(dotnet --list-sdks 2>&1)
    dotnetRuntimes    = @(dotnet --list-runtimes 2>&1 | Where-Object { $_ -match 'WindowsDesktop|NETCore.App' })
    gitVersion        = Get-ToolVersion 'git'
    nodeVersion       = Get-ToolVersion 'node'
    cpuName           = if ($cpu) { $cpu.Name.Trim() } else { 'unknown' }
    logicalProcessors = $env:NUMBER_OF_PROCESSORS
    totalMemoryMb     = if ($system) { [math]::Round($system.TotalPhysicalMemory / 1MB) } else { 0 }
    systemDrive       = $env:SystemDrive
    freeSpaceGb       = try {
        [math]::Round((Get-PSDrive -Name $env:SystemDrive.TrimEnd(':') -ErrorAction Stop).Free / 1GB, 1)
    }
    catch { 0 }
    tasksFolderReadable = Test-Path 'C:\Windows\System32\Tasks' -ErrorAction SilentlyContinue
    nugetSources      = @(dotnet nuget list source 2>&1)
}

if ($Json) {
    $report | ConvertTo-Json -Depth 4
    exit 0
}

Write-Host '===== RubbishCleaner 环境探测 =====' -ForegroundColor Cyan
foreach ($key in $report.Keys) {
    $value = $report[$key]
    if ($value -is [array]) {
        Write-Host ("{0,-20}: " -f $key) -NoNewline
        Write-Host ''
        $value | ForEach-Object { Write-Host "    $_" }
    }
    else {
        Write-Host ("{0,-20}: {1}" -f $key, $value)
    }
}

Write-Host ''
if (-not $isAdmin) {
    Write-Host '提示：当前为标准用户。计划任务扫描将降级，主程序启动会请求 UAC。' -ForegroundColor Yellow
}
else {
    Write-Host '当前为管理员权限，可执行完整扫描。' -ForegroundColor Green
}
