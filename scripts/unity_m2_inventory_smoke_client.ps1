param(
    [string]$ClientExe = "",
    [string]$LogPath = "",
    [string]$RunId = "",
    [string]$ItemDefinitionId = "stone",
    [int]$Quantity = 5,
    [int]$TargetSlot = 2,
    [int]$DropQuantity = 1,
    [int]$TimeoutSeconds = 45,
    [switch]$StopExisting
)

$ErrorActionPreference = "Stop"

$ProjectPath = (Resolve-Path "$PSScriptRoot\..\unity\SurvivalWorld").Path
if ([string]::IsNullOrWhiteSpace($ClientExe)) {
    $ClientExe = Join-Path $ProjectPath "Build\Client\survival.exe"
}

if (-not (Test-Path -LiteralPath $ClientExe)) {
    throw "Client executable not found: $ClientExe"
}

$LogsPath = Join-Path $ProjectPath "Logs"
New-Item -ItemType Directory -Force -Path $LogsPath | Out-Null

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = "m2-inventory-" + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString([Globalization.CultureInfo]::InvariantCulture)
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $LogsPath ("m2-inventory-smoke-{0}.log" -f $RunId)
}

if ($StopExisting) {
    Get-Process survival -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Resolve-Path -LiteralPath $ClientExe).Path } |
        Stop-Process -Force
}

if (Test-Path -LiteralPath $LogPath) {
    Clear-Content -LiteralPath $LogPath
}

$argsList = @(
    "--sw-auto-connect",
    "--sw-m2-inventory-smoke",
    "--sw-m2-inventory-run-id", $RunId,
    "--sw-m2-inventory-item", $ItemDefinitionId,
    "--sw-m2-inventory-quantity", $Quantity.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--sw-m2-inventory-target-slot", $TargetSlot.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--sw-m2-inventory-drop-quantity", $DropQuantity.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-logFile", $LogPath,
    "-screen-fullscreen", "0",
    "-screen-width", "800",
    "-screen-height", "600"
)

$process = Start-Process -FilePath $ClientExe -ArgumentList $argsList -WindowStyle Hidden -PassThru
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$required = @(
    "FishNet client authenticated.",
    "Submitted M2 inventory smoke commands: run_id=$RunId",
    "Inventory command result: op=ADD, status=Ok",
    "Inventory command result: op=Move, status=Ok",
    "Inventory command result: op=Drop, status=Ok"
)

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $LogPath) {
        $log = Get-Content -Raw -LiteralPath $LogPath
        $missing = @($required | Where-Object { $log -notlike "*$_*" })
        $fatal = $log -match "SceneId of .* not found|Exception|ServerRpc not found|TargetRpc not found"
        if ($missing.Count -eq 0 -and -not $fatal) {
            [pscustomobject]@{
                ok = $true
                process_id = $process.Id
                run_id = $RunId
                log_path = $LogPath
            }
            exit 0
        }
    }

    Start-Sleep -Seconds 1
}

$tail = if (Test-Path -LiteralPath $LogPath) { Get-Content -LiteralPath $LogPath -Tail 80 } else { @("Log file was not created: $LogPath") }
Write-Error ("M2 inventory smoke did not complete within {0}s. PID={1}, RunId={2}`n{3}" -f $TimeoutSeconds, $process.Id, $RunId, ($tail -join [Environment]::NewLine))