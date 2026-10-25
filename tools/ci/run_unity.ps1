# Run Unity in batch mode, stream interesting log lines, return Unity's exit code.
#   run_unity.ps1 -Unity <Unity.exe> -Log <file> -- <unity arguments...>
param(
  [Parameter(Mandatory = $true)][string]$Unity,
  [Parameter(Mandatory = $true)][string]$Log,
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$UnityArgs
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Split-Path $Log) | Out-Null
if (Test-Path $Log) { Remove-Item $Log -Force }
function Quote([string]$a) { if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }
$argLine = (@('-batchmode', '-nographics', '-logFile', $Log) + $UnityArgs | ForEach-Object { Quote $_ }) -join ' '
Write-Host "> $Unity $argLine"
$p = Start-Process -FilePath $Unity -ArgumentList $argLine -PassThru -NoNewWindow
# Windows PowerShell only reports ExitCode if the handle was opened while the
# process was running
$null = $p.Handle
$pattern = '\[IOSPort\]|error CS|Exception|Error building|Build (succeeded|failed)|Aborting batchmode|license|License|Importing|Compiling|Exiting'
$pos = 0
while (-not $p.HasExited) {
  Start-Sleep -Seconds 15
  if (Test-Path $Log) {
    $lines = @(Get-Content $Log -ErrorAction SilentlyContinue)
    if ($lines -and $lines.Count -gt $pos) {
      $lines[$pos..($lines.Count - 1)] | Where-Object { $_ -match $pattern } | Select-Object -First 200 | ForEach-Object { Write-Host $_ }
      $pos = $lines.Count
    }
  }
}
$p.WaitForExit()
if (Test-Path $Log) {
  $lines = @(Get-Content $Log)
  if ($lines.Count -gt $pos) { $lines[$pos..($lines.Count - 1)] | Where-Object { $_ -match $pattern } | ForEach-Object { Write-Host $_ } }
}
$code = $p.ExitCode
if ($null -eq $code) { Write-Host 'Unity exit code unavailable'; $code = 1 }
Write-Host "Unity exited with code $code"
exit $code
