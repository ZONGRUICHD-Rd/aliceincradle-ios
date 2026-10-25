# Get the game archive onto the runner and print its path.
#   fetch_archive.ps1 -Out <dir> [-LocalPath <file>] -Repo <owner/name> [-Branch game-archive]
#
# -LocalPath (repository variable GAME_ARCHIVE_PATH) points at the .7z on a
# local disk or NAS share: used as-is, nothing is downloaded.
# Otherwise the split parts on the game-archive branch are downloaded one by
# one with curl, resuming interrupted transfers, and checked against SHA256SUMS
# (a single git fetch of the whole branch cannot resume on flaky connections).
# The assembled archive stays in -Out, so later runs reuse it.
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$LocalPath = '',
  [Parameter(Mandatory = $true)][string]$Repo,
  [string]$Branch = 'game-archive'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null

if ($LocalPath) {
  if (-not (Test-Path -LiteralPath $LocalPath)) { throw "GAME_ARCHIVE_PATH not found: $LocalPath" }
  Write-Host "using local archive $LocalPath"
  Write-Output (Resolve-Path -LiteralPath $LocalPath).Path
  exit 0
}

$base = "https://raw.githubusercontent.com/$Repo/$Branch/archive"
# public repository: anonymous; the token is only sent after an HTTP error
# (private repository)
$script:auth = @()

# Get-FileHash is missing when the runner's PSModulePath points at PowerShell 7
# modules, so hash with .NET directly
function Get-Sha256([string]$path) {
  $sha = [Security.Cryptography.SHA256]::Create()
  $fs = [IO.File]::OpenRead($path)
  try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLower() }
  finally { $fs.Dispose(); $sha.Dispose() }
}

function Get-File([string]$name, [string]$expected) {
  $dest = Join-Path $Out $name
  for ($attempt = 1; $attempt -le 8; $attempt++) {
    if ($expected -and (Test-Path $dest) -and ((Get-Sha256 $dest) -eq $expected)) {
      Write-Host "  $name ok"
      return $dest
    }
    Write-Host "  downloading $name (attempt $attempt)"
    # -C - resumes a partial file; curl retries transient errors itself as well
    & curl.exe -fL --no-progress-meter --retry 10 --retry-all-errors --retry-delay 5 --connect-timeout 30 `
      --speed-limit 1024 --speed-time 120 -C - @script:auth -o $dest "$base/$name"
    if ($LASTEXITCODE -ne 0) {
      Write-Host "  curl exited with $LASTEXITCODE"
      if ($LASTEXITCODE -eq 22 -or $LASTEXITCODE -eq 33 -or $LASTEXITCODE -eq 36) { Remove-Item $dest -ErrorAction SilentlyContinue }
      if ($LASTEXITCODE -eq 22 -and $env:GITHUB_TOKEN -and $script:auth.Count -eq 0) {
        $script:auth = @('-H', "Authorization: token $env:GITHUB_TOKEN")
      }
      Start-Sleep -Seconds (5 * $attempt)
      continue
    }
    if (-not $expected) { return $dest }
    if ((Get-Sha256 $dest) -eq $expected) { Write-Host "  $name ok"; return $dest }
    Write-Host "  checksum mismatch for $name, downloading again"
    Remove-Item $dest -Force
  }
  throw "failed to download $name"
}

Remove-Item (Join-Path $Out 'SHA256SUMS') -ErrorAction SilentlyContinue
$sums = Get-File 'SHA256SUMS' ''
$entries = Get-Content $sums | Where-Object { $_ -match '^\s*([0-9a-fA-F]{64})\s+\*?(\S.*)$' } |
  ForEach-Object { [pscustomobject]@{ Hash = $Matches[1].ToLower(); Name = $Matches[2].Trim() } }
$whole = $entries | Where-Object { $_.Name -notmatch '\.part\d+$' } | Select-Object -First 1
$parts = $entries | Where-Object { $_.Name -match '\.part\d+$' } | Sort-Object Name
if (-not $whole -or -not $parts) { throw 'SHA256SUMS has an unexpected format' }

$archive = Join-Path $Out $whole.Name
if ((Test-Path $archive) -and ((Get-Sha256 $archive) -eq $whole.Hash)) {
  Write-Host "cached archive ok: $archive"
  Write-Output $archive
  exit 0
}

foreach ($p in $parts) { Get-File $p.Name $p.Hash | Out-Null }

$fs = [IO.File]::Create($archive)
try {
  foreach ($p in $parts) {
    $in = [IO.File]::OpenRead((Join-Path $Out $p.Name))
    try { $in.CopyTo($fs) } finally { $in.Dispose() }
  }
} finally { $fs.Dispose() }
foreach ($p in $parts) { Remove-Item (Join-Path $Out $p.Name) }
$hash = Get-Sha256 $archive
if ($hash -ne $whole.Hash) { throw "archive checksum mismatch: $hash" }
Write-Host "archive ok: $archive"
Write-Output $archive
