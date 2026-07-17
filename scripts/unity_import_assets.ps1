param(
    [string]$Unity = $env:UNITY_EXE
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Unity)) {
    $Unity = "C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe"
}

if (-not (Test-Path -LiteralPath $Unity)) {
    throw "Unity executable not found: $Unity. Set UNITY_EXE or pass -Unity."
}

$ProjectPath = (Resolve-Path "$PSScriptRoot\..\unity\SurvivalWorld").Path
$LogPath = Join-Path $ProjectPath "Logs\unity-import-assets.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null

function Invoke-Unity {
    param([string[]]$Arguments)

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $Unity
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$processInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($processInfo)
    $process.WaitForExit()
    return $process.ExitCode
}

$exitCode = Invoke-Unity -Arguments @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-logFile",
    $LogPath,
    "-projectPath",
    $ProjectPath,
    "-executeMethod",
    "BuildScript.ImportAssets"
)

if ($exitCode -ne 0) { exit $exitCode }
