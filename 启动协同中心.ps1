$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$CliArgs = @($args)

$localExe = Join-Path $PSScriptRoot 'AI协同中心.exe'
$outputsDir = Join-Path $PSScriptRoot 'outputs'
if (Test-Path -LiteralPath $outputsDir) {
    $latestDir = Get-ChildItem -LiteralPath $outputsDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'AI协同中心\AI协同中心.exe')) } |
        Sort-Object { [version]$_.Name.Substring(1) } -Descending |
        Select-Object -First 1

    if (-not $latestDir) {
        throw "未在 $outputsDir 下找到完整的已验证发布版本。"
    }
    $exePath = Join-Path $latestDir.FullName 'AI协同中心\AI协同中心.exe'
} elseif (Test-Path -LiteralPath $localExe) {
    $exePath = $localExe
} else {
    throw "未找到 AI协同中心.exe，请检查发布目录是否完整。"
}

function Test-HubProcessRunning {
    param([string]$ExeFullPath)
    $procs = @(Get-Process -Name 'AI协同中心' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return $false }
    if ([string]::IsNullOrWhiteSpace($ExeFullPath)) { return $true }
    $same = @($procs | Where-Object { $_.Path -eq $ExeFullPath })
    return ($same.Count -gt 0)
}

function Try-LaunchViaOnceSchtasks {
    param(
        [Parameter(Mandatory = $true)][string]$ExeFullPath
    )
    $taskName = 'AICollabHub_GuiLaunch'
    $st = (Get-Date).AddMinutes(5).ToString('HH:mm')
    $tr = "`"$ExeFullPath`""
    $prevEa = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        cmd /c "schtasks /Create /TN $taskName /TR $tr /SC ONCE /ST $st /IT /F >nul 2>&1"
        cmd /c "schtasks /Run /TN $taskName >nul 2>&1"
        Start-Sleep -Milliseconds 1200
        cmd /c "schtasks /Delete /TN $taskName /F >nul 2>&1"
    } catch {
    } finally {
        $ErrorActionPreference = $prevEa
    }
}

function Try-LaunchViaWmi {
    param([Parameter(Mandatory = $true)][string]$ExeFullPath)
    $prevEa = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $alive = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='AI协同中心.exe'" -ErrorAction SilentlyContinue)
        if ($alive.Count -eq 0) {
            $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
                CommandLine      = "`"$ExeFullPath`""
                CurrentDirectory = (Split-Path -Parent $ExeFullPath)
            } -ErrorAction SilentlyContinue
        }
    } catch {
        try {
            $null = ([wmiclass]'Win32_Process').Create("`"$ExeFullPath`"")
        } catch {}
    } finally {
        $ErrorActionPreference = $prevEa
    }
}

if ($CliArgs.Count -gt 0) {
    $escaped = @('--cli')
    foreach ($a in $CliArgs) {
        if ($a -ne '-Cli' -and $a -ne '--cli') {
            if ($a -match '\s') {
                $escaped += ('"' + ($a -replace '"', '\"') + '"')
            } else {
                $escaped += $a
            }
        }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.WorkingDirectory = Split-Path -Parent $exePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.Arguments = ($escaped -join ' ')

    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTxt = $proc.StandardOutput.ReadToEnd()
    $errTxt = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    if ($outTxt) { Write-Output $outTxt.TrimEnd() }
    if ($errTxt) { Write-Error $errTxt.TrimEnd() }
    exit $proc.ExitCode
}

# 清理历史永久 MINUTE 心跳任务（若存在）
try {
    cmd /c "schtasks /Delete /TN AICollabHub_KeepAlive_v199_Min /F >nul 2>&1"
    cmd /c "schtasks /Delete /TN AICollabHub_KeepAlive_v199 /F >nul 2>&1"
} catch {}

$running = @(Get-Process -Name 'AI协同中心' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $sameVer = @($running | Where-Object { $_.Path -eq $exePath })
    if ($sameVer.Count -gt 0) {
        if ($sameVer.Count -gt 1) {
            $sameVer | Select-Object -Skip 1 | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        Write-Output ("检测到最新版 AI协同中心 已在运行 (PID=" + ($sameVer[0].Id) + ")，直接复用现有窗口。")
        exit 0
    }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

Start-Process explorer.exe -ArgumentList "`"$exePath`""
Start-Sleep -Milliseconds 900

if (-not (Test-HubProcessRunning -ExeFullPath $exePath)) {
    Try-LaunchViaOnceSchtasks -ExeFullPath $exePath
}

if (-not (Test-HubProcessRunning -ExeFullPath $exePath)) {
    Try-LaunchViaWmi -ExeFullPath $exePath
    Start-Sleep -Milliseconds 800
}

if (Test-HubProcessRunning -ExeFullPath $exePath) {
    Write-Output ("已启动最新版 AI协同中心：" + $exePath)
} else {
    Write-Output ("已尝试启动 AI协同中心（explorer/schtasks/WMI），请确认进程：" + $exePath)
}
