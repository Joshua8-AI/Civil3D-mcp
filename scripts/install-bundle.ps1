<#
.SYNOPSIS
  Installs the plugin as an Autodesk ApplicationPlugins bundle (auto-load).

.DESCRIPTION
  The APPLOAD Startup Suite refuses folders that are not in TRUSTEDPATHS when
  SECURELOAD=1, which is the default and which reports failure only as a bare
  "Error" dialog. Bundles under ApplicationPlugins are Autodesk's supported
  deployment path and are implicitly trusted, so this sidesteps both TRUSTEDPATHS
  and the Startup Suite.

  Installs per-user under %APPDATA%, so no elevation is required.

.PARAMETER Uninstall
  Remove the bundle instead of installing it.
#>
param(
  [string] $SourceDir,
  [string] $BundleRoot = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"),
  [string] $BundleName = "Civil3DMcp.bundle",
  [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$bundleDir = Join-Path $BundleRoot $BundleName
$contents  = Join-Path $bundleDir "Contents"

if ($Uninstall) {
  if (Test-Path $bundleDir) {
    Remove-Item $bundleDir -Recurse -Force
    Write-Host "Removed $bundleDir"
  } else {
    Write-Host "Nothing to remove at $bundleDir"
  }
  Write-Host "Restart Civil 3D for the change to take effect."
  exit 0
}

if (-not $SourceDir) {
  $SourceDir = Join-Path $repoRoot "Civil3D-MCP-Plugin\bin\Release\net10.0-windows"
}
$dll = Join-Path $SourceDir "Civil3DMcpPlugin.dll"
if (-not (Test-Path $dll)) {
  throw "Plugin not built at '$dll'. Run scripts\build-2027.ps1 first."
}

# Civil 3D holds a lock on the bundle DLL while loaded; copying over it would
# fail partway and leave a mixed-version bundle.
$acad = Get-Process -Name acad -ErrorAction SilentlyContinue
if ($acad) {
  throw ("Civil 3D is running (pid $($acad.Id -join ', ')). Close it completely, then re-run.")
}

New-Item -ItemType Directory -Path $contents -Force | Out-Null

# Autodesk reference assemblies are resolved from the Civil 3D process and must
# not be shipped in the bundle.
$excluded = Get-ChildItem (Join-Path $repoRoot "C_References") -Filter *.dll -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name
$copied = 0
foreach ($f in Get-ChildItem $SourceDir -File) {
  if ($excluded -contains $f.Name) { continue }
  Copy-Item $f.FullName -Destination (Join-Path $contents $f.Name) -Force
  $copied++
}

$appVersion = (Get-Item $dll).VersionInfo.FileVersion
if (-not $appVersion) { $appVersion = "1.0.0.0" }

# Stable GUIDs so reinstalls upgrade in place rather than registering a duplicate.
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage
  SchemaVersion="1.0"
  AppVersion="$appVersion"
  Author="Sacred-G"
  ProductCode="{3F7C1A94-6E2D-4B85-9C13-5A8E0D2F7B41}"
  UpgradeCode="{8B24E5D7-1C3F-4A69-B0E2-7D9146C8F3A5}"
  Name="Civil 3D MCP Plugin"
  PreferNewestAcross="AppData|ProgramFiles"
  >
  <CompanyDetails Name="Sacred-G" Url="https://github.com/Sacred-G/Civil3D-mcp" Email="" />
  <RuntimeRequirements Platform="Civil3D" SeriesMin="R26.0" SeriesMax="R26.0" OS="Win64" SupportPath="./Contents" />
  <Components>
    <ComponentEntry AppName="Civil3DMcp" ModuleName="./Contents/Civil3DMcpPlugin.dll"
                    LoadOnAutoCADStartup="true" LoadOnRequest="false" AppDescription="Civil 3D MCP JSON-RPC bridge">
    </ComponentEntry>
  </Components>
  <DisplayInAppManager>true</DisplayInAppManager>
</ApplicationPackage>
"@

# PackageContents.xml must be UTF-8 without BOM or AutoCAD ignores the bundle.
[System.IO.File]::WriteAllText(
  (Join-Path $bundleDir "PackageContents.xml"),
  $xml,
  (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Installed bundle: $bundleDir"
Write-Host "  PackageContents.xml  (AppVersion $appVersion)"
Write-Host "  Contents\            ($copied file(s))"
Write-Host ""
Write-Host "Close Civil 3D COMPLETELY and reopen it. The plugin auto-loads;"
Write-Host "no NETLOAD, no Startup Suite, no TRUSTEDPATHS entry needed."
Write-Host "Verify with C3DMCPSTART (echoes the port)."
