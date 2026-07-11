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
$LogsPath = Join-Path $ProjectPath "Logs"
New-Item -ItemType Directory -Force -Path $LogsPath | Out-Null

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

function Invoke-UnityTests {
    param(
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$ResultsPath,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $exitCode = Invoke-Unity -Arguments @(
        "-batchmode",
        "-runTests",
        "-projectPath",
        $ProjectPath,
        "-testPlatform",
        $Platform,
        "-testResults",
        $ResultsPath,
        "-logFile",
        $LogPath
    )

    if ($exitCode -ne 0) { exit $exitCode }
}

Invoke-UnityTests -Platform EditMode -ResultsPath "$ProjectPath\results-editmode.xml" -LogPath "$LogsPath\unity-test-editmode.log"
Invoke-UnityTests -Platform PlayMode -ResultsPath "$ProjectPath\results-playmode.xml" -LogPath "$LogsPath\unity-test-playmode.log"
