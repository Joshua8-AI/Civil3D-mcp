<#
.SYNOPSIS
  Stages the six Civil 3D managed reference assemblies into C_References/.

.DESCRIPTION
  Civil3DMcpPlugin.csproj expects all six references in one directory
  ($(Civil3DReferencesPath), default ..\C_References). A Civil 3D 2027 install
  spreads them across three folders, so the README's single-folder guidance
  does not apply:

      accoremgd / acdbmgd / acmgd   -> <AcadRoot>\
      AecBaseMgd                    -> <AcadRoot>\ACA\
      AeccDbMgd / AeccPressurePipes -> <AcadRoot>\C3D\

  C_References/ is gitignored; these are licensed Autodesk assemblies and must
  never be committed.
#>
param(
  [string] $AcadRoot = "C:\Program Files\Autodesk\AutoCAD 2027",
  [string] $Destination,
  [switch] $Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot "C_References" }

if (-not (Test-Path $AcadRoot)) {
  throw "Civil 3D install not found at '$AcadRoot'. Pass -AcadRoot <path>."
}

# Canonical file name -> subdirectory of $AcadRoot to search.
$required = [ordered]@{
  "accoremgd.dll"              = ""
  "AcDbMgd.dll"                = ""
  "acmgd.dll"                  = ""
  "AecBaseMgd.dll"             = "ACA"
  "AeccDbMgd.dll"              = "C3D"
  "AeccPressurePipesMgd.dll"   = "C3D"
}

if (-not (Test-Path $Destination)) {
  New-Item -ItemType Directory -Path $Destination | Out-Null
  Write-Host "Created $Destination"
}

$missing = @()
foreach ($name in $required.Keys) {
  $subDir = $required[$name]
  $searchDir = if ($subDir) { Join-Path $AcadRoot $subDir } else { $AcadRoot }
  $target = Join-Path $Destination $name

  if ((Test-Path $target) -and -not $Force) {
    Write-Host "  skip    $name (already staged; -Force to overwrite)"
    continue
  }

  # NTFS is case-insensitive, but match defensively so casing drift in the
  # install (acdbmgd.dll vs AcDbMgd.dll) still resolves.
  $source = Get-ChildItem -Path $searchDir -Filter $name -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
  if (-not $source) {
    Write-Warning "  MISSING $name (looked in $searchDir)"
    $missing += $name
    continue
  }

  Copy-Item -Path $source.FullName -Destination $target -Force
  $version = (Get-Item $target).VersionInfo.FileVersion
  Write-Host ("  copied  {0,-26} <- {1}  [v{2}]" -f $name, $source.DirectoryName, $version)
}

if ($missing.Count -gt 0) {
  throw "Could not locate: $($missing -join ', '). Check the Civil 3D install is complete."
}

Write-Host ""
Write-Host "All six references staged in $Destination"
