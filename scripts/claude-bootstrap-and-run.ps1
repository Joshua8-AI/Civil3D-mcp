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

# Build when there is no build yet, or when the sources changed since the last one (after a git pull,
# new tools such as civil3d_run_code appear without a manual npm run build). Output stays off stdout,
# which carries the MCP protocol.
$stale = -not (Test-Path $buildEntryPath)
if (-not $stale) {
  $builtAt = (Get-Item $buildEntryPath).LastWriteTimeUtc
  $sources = @(Get-ChildItem (Join-Path $repoRoot "src") -Recurse -File -ErrorAction SilentlyContinue) +
    @(Get-Item (Join-Path $repoRoot "package.json"), (Join-Path $repoRoot "tsconfig.json") -ErrorAction SilentlyContinue)
  $stale = [bool]($sources | Where-Object { $_.LastWriteTimeUtc -gt $builtAt } | Select-Object -First 1)
}

if ($stale) {
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
