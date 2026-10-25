# Export the Windows build as a Unity project with AssetRipper (headless).
#   run_assetripper.ps1 -AssetRipper <dir> -Game <game root> -Out <dir>
# Result: <Out>\ExportedProject\{Assets,ProjectSettings}
param(
  [Parameter(Mandatory = $true)][string]$AssetRipper,
  [Parameter(Mandatory = $true)][string]$Game,
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$Port = 8899
)
$ErrorActionPreference = 'Stop'
$url = "http://127.0.0.1:$Port"
$exe = Get-ChildItem -Path $AssetRipper -Recurse -Filter 'AssetRipper.GUI.Free.exe' | Select-Object -First 1
if (-not $exe) { throw "AssetRipper.GUI.Free.exe not found in $AssetRipper" }
$logFile = "$Out.log"
$stdout = "$Out.stdout.txt"
$stderr = "$Out.stderr.txt"

function Show-Output {
  foreach ($f in $stdout, $stderr) {
    if (Test-Path $f) { Get-Content $f -Tail 60 | ForEach-Object { Write-Host "  $_" } }
  }
  if (Test-Path $logFile) { Get-Content $logFile -Tail 100 | ForEach-Object { Write-Host "  $_" } }
}

# Talk to the local API with curl: it ignores the Windows system proxy, which
# Invoke-WebRequest would use even for 127.0.0.1.
function Post([string]$path, [string[]]$fields, [int]$timeout) {
  $a = @('-sS', '-f', '--noproxy', '*', '-m', "$timeout", '-o', 'NUL')
  foreach ($f in $fields) { $a += @('--data-urlencode', $f) }
  & curl.exe @a "$url$path"
  if ($LASTEXITCODE -ne 0) { Show-Output; throw "POST $path failed (curl $LASTEXITCODE)" }
}

# Boolean options are switches (--headless); --log defaults to true.
$proc = Start-Process -FilePath $exe.FullName -ArgumentList "--headless --port $Port --log-path `"$logFile`"" `
  -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
  $up = $false
  for ($i = 0; $i -lt 90 -and -not $up; $i++) {
    if ($proc.HasExited) { Show-Output; throw "AssetRipper exited during startup (code $($proc.ExitCode))" }
    & curl.exe -s -f --noproxy '*' -m 5 -o NUL "$url/"
    if ($LASTEXITCODE -eq 0) { $up = $true } else { Start-Sleep -Seconds 2 }
  }
  if (-not $up) { Show-Output; throw 'AssetRipper did not start' }
  # Boolean settings absent from the form are set to false.
  Post '/Settings/Update' @(
    'ScriptExportMode=DllExportWithoutRenaming', 'ShaderExportMode=Dummy',
    'BundledAssetsExportMode=DirectExport', 'StreamingAssetsMode=Extract',
    'ImageExportFormat=Png', 'SpriteExportMode=Yaml', 'TextExportMode=Parse',
    'AudioExportFormat=Default', 'EnableStaticMeshSeparation=on') 60
  Write-Host "[assetripper] loading $Game"
  $t = Measure-Command { Post '/LoadFolder' @("Path=$Game") 7200 }
  Write-Host "[assetripper] loaded in $t"
  if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
  Write-Host "[assetripper] exporting to $Out"
  $t = Measure-Command { Post '/Export/UnityProject' @("Path=$Out") 7200 }
  Write-Host "[assetripper] exported in $t"
} finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
if (-not (Test-Path (Join-Path $Out 'ExportedProject\Assets'))) {
  Show-Output
  throw 'export failed'
}
Get-ChildItem (Join-Path $Out 'ExportedProject\Assets') | Select-Object Name
