$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$nodeModulesPath = Join-Path $repoRoot "node_modules"
$buildEntryPath = Join-Path $repoRoot "build/index.js"
$npmCommand = "npm.cmd"
$nodeCommand = "node"

# stdout carries the MCP protocol, so only node may write to it: npm output goes to this log (started
# afresh by each launch that runs npm), and on failure its last lines go to stderr.
$logPath = Join-Path $env:TEMP "civil3d-mcp-bootstrap.log"
$logStarted = $false
# Stamps written only after a successful install or build. The build stamp sits in build\, which the
# build's prebuild step deletes first, so a failed or killed build leaves none and the next launch builds
# again instead of running a half-built server.
$depsStampPath = Join-Path $nodeModulesPath ".civil3d-mcp-deps"
$buildStampPath = Join-Path $repoRoot "build/.civil3d-mcp-inputs"

if (-not (Get-Command $nodeCommand -ErrorAction SilentlyContinue)) {
  throw "Node.js was not found on PATH."
}

if (-not (Get-Command $npmCommand -ErrorAction SilentlyContinue)) {
  throw "npm was not found on PATH."
}

function Invoke-NpmQuiet {
  # Function scope only: under Windows PowerShell 5.1 a native stderr line (npm's update notice, a
  # warning) would otherwise end the script as a NativeCommandError.
  $ErrorActionPreference = "Continue"
  "== $(Get-Date -Format s) npm $args" | Out-File -LiteralPath $logPath -Append:$script:logStarted -Encoding utf8
  $script:logStarted = $true
  & $npmCommand @args 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $logPath -Append -Encoding utf8
  $LASTEXITCODE
}

function Stop-Bootstrap([string] $What) {
  [Console]::Error.WriteLine("$What. Log: $logPath")
  Get-Content -LiteralPath $logPath -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { [Console]::Error.WriteLine($_) }
  exit 1
}

function Read-Stamp([string] $Path) {
  if (Test-Path -LiteralPath $Path) { [IO.File]::ReadAllText($Path) } else { "" }
}

function Write-Stamp([string] $Path, [string] $Value) {
  # A missing stamp only costs one more install or build next time: never fail the launch over it.
  try { [IO.File]::WriteAllText($Path, $Value) }
  catch { [Console]::Error.WriteLine("Could not write ${Path}: $($_.Exception.Message)") }
}

# Content hash of the dependency manifests.
function Get-DependencyHash {
  (@("package.json", "package-lock.json") | ForEach-Object {
    $file = Join-Path $repoRoot $_
    if (Test-Path -LiteralPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { "none" }
  }) -join "|"
}

# Every build input with its size and time, so edits, additions, deletions and renames all count.
function Get-BuildInputs {
  $inputs = @(Get-ChildItem (Join-Path $repoRoot "src") -Recurse -File -ErrorAction SilentlyContinue) +
    @("package.json", "package-lock.json", "tsconfig.json", "autodesk_videos.json", "scripts/copy-static-assets.mjs" |
      ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ })
  ($inputs | ForEach-Object { "{0}|{1}|{2}" -f $_.FullName.Substring($repoRoot.Length), $_.Length, $_.LastWriteTimeUtc.Ticks } | Sort-Object) -join "`n"
}

# Install when there are no dependencies yet or package.json / package-lock.json changed since the last
# install (a pull that adds or bumps one). The hash is taken after the install, which may rewrite the lock.
if (-not (Test-Path $nodeModulesPath) -or (Read-Stamp $depsStampPath) -cne (Get-DependencyHash)) {
  if ((Invoke-NpmQuiet install --no-fund --no-audit --prefix $repoRoot) -ne 0) {
    Stop-Bootstrap "Failed to install npm dependencies for civil3d-mcp"
  }
  Write-Stamp $depsStampPath (Get-DependencyHash)
}

# Build when there is no build yet or a build input changed since the last successful build (after a git
# pull, new tools such as civil3d_run_code appear without a manual npm run build).
$buildInputs = Get-BuildInputs
if (-not (Test-Path $buildEntryPath) -or (Read-Stamp $buildStampPath) -cne $buildInputs) {
  if ((Invoke-NpmQuiet run build --prefix $repoRoot) -ne 0) {
    Stop-Bootstrap "Failed to build civil3d-mcp"
  }
  Write-Stamp $buildStampPath $buildInputs
}

# This fork's plugin listens on 8757 (see PluginRuntime.Port), not the
# upstream 8080 default the Node side would otherwise assume. Respect an
# explicit override from the environment.
if (-not $env:CIVIL3D_PORT) {
  $env:CIVIL3D_PORT = "8757"
}

& $nodeCommand $buildEntryPath
exit $LASTEXITCODE
