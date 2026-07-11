<#
.SYNOPSIS
  Blender asset generation + validation (Windows native entry point).
  On Linux/WSL use scripts/ci_assets.sh (make assets). Both call the same
  assets-pipeline/generate.py and assets-pipeline/validate.py.

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less
  scripts using the ANSI code page, so non-ASCII bytes can corrupt parsing.
  This file is saved as UTF-8 with BOM + CRLF for safety.

.EXAMPLE
  .\scripts\assets.ps1
  .\scripts\assets.ps1 -Seed 1 -ModuleSize 4 -Out build/assets
  .\scripts\assets.ps1 -Blender "C:\Program Files\Blender Foundation\Blender 4.5\blender.exe"
#>
[CmdletBinding()]
param(
  [string]$Blender = $env:BLENDER,
  [int]$Seed = 1,
  [int]$ModuleSize = 4,
  [string]$Out = "build/assets"
)
$ErrorActionPreference = "Stop"

# Move to the repository root (parent of scripts/). $PSScriptRoot can be empty
# when invoked via -File with a relative path, so resolve it robustly.
$scriptPath = $MyInvocation.MyCommand.Path
if (-not $scriptPath) { $scriptPath = $PSCommandPath }
if ($scriptPath) {
  $scriptDir = Split-Path -Parent $scriptPath
} elseif ($PSScriptRoot) {
  $scriptDir = $PSScriptRoot
} else {
  $scriptDir = (Get-Location).Path
}
$root = Split-Path -Parent $scriptDir
if (-not $root) { $root = (Get-Location).Path }
Set-Location $root
Write-Host "Repo root: $root"

function Resolve-Blender([string]$explicit) {
  if ($explicit) { return $explicit }
  foreach ($name in @("blender.exe", "blender")) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
  }
  # Search Program Files (newest version first).
  $base = "C:\Program Files\Blender Foundation"
  if (Test-Path $base) {
    $dirs = Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
    foreach ($d in $dirs) {
      $exe = Join-Path $d.FullName "blender.exe"
      if (Test-Path $exe) { return $exe }
    }
  }
  return $null
}

$blenderExe = Resolve-Blender $Blender
if (-not $blenderExe) {
  Write-Error "Blender not found. Pass -Blender <path> or install Blender."
  exit 1
}
Write-Host "Using Blender: $blenderExe"

Write-Host "== generate (seed=$Seed size=$ModuleSize out=$Out) =="
& $blenderExe --background --python assets-pipeline/generate.py -- --seed $Seed --module-size $ModuleSize --out $Out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== validate =="
# Prefer a standalone Python (py/python); fall back to Blender's bundled Python.
$py = Get-Command py -ErrorAction SilentlyContinue
if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
if ($py) {
  & $py.Source assets-pipeline/validate.py --in $Out
} else {
  Write-Host "No standalone Python found - validating with Blender's bundled Python."
  & $blenderExe --background --python assets-pipeline/validate.py -- --in $Out
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "assets.ps1: OK"
