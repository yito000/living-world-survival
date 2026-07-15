param(
    [int]$DurationSeconds = 60,
    [int]$StartupTimeoutSeconds = 30,
    [string]$WslDistro = "Ubuntu-26.04",
    [string]$ServerBuildPath = "unity\SurvivalWorld\Build\Server\survival-server.x86_64",
    [string]$LogPath = "build\e2e\m4_ai_soak_smoke.log",
    [switch]$AllowEarlyExit
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path "$PSScriptRoot\..").Path
$ServerExe = Join-Path $Root $ServerBuildPath
$ResolvedLogPath = Join-Path $Root $LogPath
$LogDir = Split-Path -Parent $ResolvedLogPath
$ExitCodePath = Join-Path $LogDir "m4_ai_soak_smoke.exitcode"
$WrapperLogPath = Join-Path $LogDir "m4_ai_soak_smoke.wrapper.log"
$WrapperScriptPath = Join-Path $LogDir "m4_ai_soak_smoke_runner.sh"

if ($DurationSeconds -lt 5) {
    throw "DurationSeconds must be at least 5."
}

if ($StartupTimeoutSeconds -lt 1) {
    throw "StartupTimeoutSeconds must be at least 1."
}

if (-not (Test-Path -LiteralPath $ServerExe)) {
    throw "Dedicated Server build not found: $ServerExe. Run scripts\unity_build_server.ps1 first."
}

$Wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
if ($null -eq $Wsl) {
    throw "wsl.exe not found. This smoke runs the Linux Dedicated Server build through WSL."
}

& wsl.exe -d $WslDistro -- true
if ($LASTEXITCODE -ne 0) {
    throw "WSL distro is not available or failed to start: $WslDistro"
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
foreach ($path in @($ResolvedLogPath, $ExitCodePath, $WrapperLogPath, $WrapperScriptPath)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

function ConvertTo-WslPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $normalizedPath = $Path -replace '\\', '/'
    $converted = & wsl.exe -d $WslDistro -- wslpath -a $normalizedPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($converted)) {
        throw "Failed to convert path for WSL distro ${WslDistro}: $Path"
    }

    return $converted.Trim()
}

$ServerExeWsl = ConvertTo-WslPath $ServerExe
$ServerDirWsl = ConvertTo-WslPath (Split-Path -Parent $ServerExe)
$LogPathWsl = ConvertTo-WslPath $ResolvedLogPath
$ExitCodePathWsl = ConvertTo-WslPath $ExitCodePath
$WrapperLogPathWsl = ConvertTo-WslPath $WrapperLogPath
$WrapperScriptPathWsl = ConvertTo-WslPath $WrapperScriptPath
$Duration = $DurationSeconds

Write-Host "== M4 AI soak smoke =="
Write-Host "WSL:    $WslDistro"
Write-Host "Server: $ServerExe"
Write-Host "Log:    $ResolvedLogPath"
Write-Host "Window: ${Duration}s"

$scriptLines = @(
    '#!/usr/bin/env bash',
    'set +e',
    'cd "__SERVER_DIR__"',
    'chmod +x "__SERVER_EXE__"',
    'rm -f "__LOG_PATH__" "__EXIT_CODE_PATH__" "__WRAPPER_LOG_PATH__"',
    'timeout -k 10s __DURATION__s "__SERVER_EXE__" -batchmode -nographics -logFile "__LOG_PATH__" > "__WRAPPER_LOG_PATH__" 2>&1',
    'rc=$?',
    'printf ''%s'' "$rc" > "__EXIT_CODE_PATH__"',
    'if [ ! -f "__LOG_PATH__" ]; then',
    '  cp "__WRAPPER_LOG_PATH__" "__LOG_PATH__"',
    'elif [ -s "__WRAPPER_LOG_PATH__" ]; then',
    '  printf ''\n== wrapper output ==\n'' >> "__LOG_PATH__"',
    '  cat "__WRAPPER_LOG_PATH__" >> "__LOG_PATH__"',
    'fi',
    'if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ] || [ "$rc" -eq 143 ]; then',
    '  exit 0',
    'fi',
    'exit "$rc"'
)

$script = [string]::Join("`n", $scriptLines)
$script = $script.Replace('__SERVER_DIR__', $ServerDirWsl)
$script = $script.Replace('__SERVER_EXE__', $ServerExeWsl)
$script = $script.Replace('__LOG_PATH__', $LogPathWsl)
$script = $script.Replace('__EXIT_CODE_PATH__', $ExitCodePathWsl)
$script = $script.Replace('__WRAPPER_LOG_PATH__', $WrapperLogPathWsl)
$script = $script.Replace('__DURATION__', $Duration.ToString([System.Globalization.CultureInfo]::InvariantCulture))
[System.IO.File]::WriteAllText($WrapperScriptPath, $script + "`n", [System.Text.UTF8Encoding]::new($false))

& wsl.exe -d $WslDistro -- bash $WrapperScriptPathWsl
$wslExitCode = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $ResolvedLogPath)) {
    throw "Smoke log was not created: $ResolvedLogPath"
}

$unityExitCode = -1
if (Test-Path -LiteralPath $ExitCodePath) {
    $rawExitCode = Get-Content -Raw -LiteralPath $ExitCodePath
    if ($null -ne $rawExitCode) {
        $rawExitCode = $rawExitCode.Trim()
        if (-not [int]::TryParse($rawExitCode, [ref]$unityExitCode)) {
            $unityExitCode = -1
        }
    }
}

$log = Get-Content -Raw -LiteralPath $ResolvedLogPath
$expectedTimeout = $unityExitCode -eq 124 -or $unityExitCode -eq 137 -or $unityExitCode -eq 143
if (-not $expectedTimeout -and -not $AllowEarlyExit) {
    throw "Dedicated Server exited before the soak window completed. unity_exit_code=$unityExitCode wsl_exit_code=$wslExitCode. See $ResolvedLogPath"
}

$requiredMarker = "AIActorSystem configured with 20 actors."
if ($log.IndexOf($requiredMarker, [System.StringComparison]::Ordinal) -lt 0) {
    throw "AIActorSystem startup marker was not found: '$requiredMarker'. See $ResolvedLogPath"
}

$fatalPatterns = @(
    "Unhandled Exception",
    "NullReferenceException",
    "MissingMethodException",
    "TypeLoadException",
    "Fatal error",
    "Crash!!!",
    "Aborting batchmode due to failure"
)

$requiredDecisionMarkers = @(
    "AI NATS connected:",
    "AI NATS subscribed:",
    "AI decision request published:",
    "AI decision result received:",
    "AI decision applied:"
)

foreach ($marker in $requiredDecisionMarkers) {
    if ($log.IndexOf($marker, [System.StringComparison]::Ordinal) -lt 0) {
        throw "AI decision-loop marker was not found: '$marker'. See $ResolvedLogPath"
    }
}

$rejectedMarker = "AI decision rejected:"
if ($log.IndexOf($rejectedMarker, [System.StringComparison]::Ordinal) -ge 0) {
    throw "AI decision rejection was found during smoke. See $ResolvedLogPath"
}
foreach ($pattern in $fatalPatterns) {
    if ($log.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Fatal log pattern found during AI smoke: '$pattern'. See $ResolvedLogPath"
    }
}

Write-Host "ai_soak_smoke: OK"
Write-Host "  WSL distro: $WslDistro"
Write-Host "  AI marker found: $requiredMarker"
Write-Host "  Log: $ResolvedLogPath"


