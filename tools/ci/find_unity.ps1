# Locate Unity.exe for a given editor version installed by Unity Hub.
#   find_unity.ps1 -Version 2022.3.62f2   -> prints the full path to Unity.exe
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$cands = New-Object System.Collections.Generic.List[string]
if ($env:UNITY_EXE) { $cands.Add($env:UNITY_EXE) }

# Hub 3 records installed editors in editors-v2.json (location is an array of paths)
foreach ($f in @('editors-v2.json', 'editors.json')) {
  $p = Join-Path $env:APPDATA "UnityHub\$f"
  if (Test-Path $p) {
    try {
      $j = Get-Content $p -Raw | ConvertFrom-Json
      $items = @()
      if ($j.data) { $items = $j.data } else { $items = $j.PSObject.Properties | ForEach-Object { $_.Value } }
      foreach ($e in $items) {
        if ($e.version -eq $Version -or "$($e.location)" -like "*$Version*") {
          foreach ($loc in @($e.location)) {
            if ($loc -like '*.exe') { $cands.Add($loc) } else { $cands.Add((Join-Path $loc 'Unity.exe')); $cands.Add((Join-Path $loc 'Editor\Unity.exe')) }
          }
        }
      }
    } catch { Write-Host "could not parse $p : $_" }
  }
}
$sec = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
if (Test-Path $sec) {
  $root = (Get-Content $sec -Raw).Trim().Trim('"')
  if ($root) { $cands.Add((Join-Path $root "$Version\Editor\Unity.exe")) }
}
foreach ($d in (Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Root })) {
  $cands.Add((Join-Path $d "Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"))
  $cands.Add((Join-Path $d "Unity\Hub\Editor\$Version\Editor\Unity.exe"))
  $cands.Add((Join-Path $d "Unity Hub\Editor\$Version\Editor\Unity.exe"))
  $cands.Add((Join-Path $d "Unity\$Version\Editor\Unity.exe"))
  $cands.Add((Join-Path $d "Unity Editors\$Version\Editor\Unity.exe"))
}
foreach ($c in $cands) {
  if ($c -and (Test-Path $c)) { Write-Output (Resolve-Path $c).Path; exit 0 }
}
Write-Error "Unity $Version not found. Set the UNITY_EXE environment variable (or repository variable) to the full path of Unity.exe. Checked:`n$($cands -join "`n")"
exit 1
