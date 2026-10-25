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
$proc = Start-Process -FilePath $exe.FullName -ArgumentList "--headless --port $Port --log true --log-path `"$logFile`"" -PassThru -WindowStyle Hidden
try {
  $up = $false
  for ($i = 0; $i -lt 90 -and -not $up; $i++) {
    try { Invoke-WebRequest -UseBasicParsing "$url/" -TimeoutSec 5 | Out-Null; $up = $true } catch { Start-Sleep -Seconds 2 }
  }
  if (-not $up) { throw 'AssetRipper did not start' }
  # Boolean settings absent from the form are set to false.
  $settings = @{
    ScriptExportMode = 'DllExportWithoutRenaming'; ShaderExportMode = 'Dummy'
    BundledAssetsExportMode = 'DirectExport'; StreamingAssetsMode = 'Extract'
    ImageExportFormat = 'Png'; SpriteExportMode = 'Yaml'; TextExportMode = 'Parse'
    AudioExportFormat = 'Default'; EnableStaticMeshSeparation = 'on'
  }
  Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/Settings/Update" -Body $settings -TimeoutSec 60 | Out-Null
  Write-Host "[assetripper] loading $Game"
  $t = Measure-Command { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/LoadFolder" -Body @{ Path = $Game } -TimeoutSec 7200 | Out-Null }
  Write-Host "[assetripper] loaded in $t"
  if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
  Write-Host "[assetripper] exporting to $Out"
  $t = Measure-Command { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/Export/UnityProject" -Body @{ Path = $Out } -TimeoutSec 7200 | Out-Null }
  Write-Host "[assetripper] exported in $t"
} finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
if (-not (Test-Path (Join-Path $Out 'ExportedProject\Assets'))) {
  if (Test-Path $logFile) { Get-Content $logFile -Tail 100 }
  throw 'export failed'
}
Get-ChildItem (Join-Path $Out 'ExportedProject\Assets') | Select-Object Name
