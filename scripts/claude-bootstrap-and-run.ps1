$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$nodeModulesPath = Join-Path $repoRoot "node_modules"
$buildEntryPath = Join-Path $repoRoot "build/index.js"
$npmCommand = "npm.cmd"
$nodeCommand = "node"

if (-not (Get-Command $nodeCommand -ErrorAction SilentlyContinue)) {
  throw "Node.js was not found on PATH."
}

if (-not (Get-Command $npmCommand -ErrorAction SilentlyContinue)) {
  throw "npm was not found on PATH."
}

if (-not (Test-Path $nodeModulesPath)) {
  & $npmCommand install --no-fund --no-audit --silent --prefix $repoRoot *> $null
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to install npm dependencies for civil3d-mcp."
  }
}

if (-not (Test-Path $buildEntryPath)) {
  & $npmCommand run build --prefix $repoRoot *> $null
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to build civil3d-mcp."
  }
}

# This fork's plugin listens on 8757 (see PluginRuntime.Port), not the
# upstream 8080 default the Node side would otherwise assume. Respect an
# explicit override from the environment.
if (-not $env:CIVIL3D_PORT) {
  $env:CIVIL3D_PORT = "8757"
}

& $nodeCommand $buildEntryPath
exit $LASTEXITCODE
