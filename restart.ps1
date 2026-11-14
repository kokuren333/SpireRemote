# Restart the SpireRemote host task and verify it answers.
$ErrorActionPreference = 'Continue'
$dir = $PSScriptRoot
$taskName = 'SpireRemoteHost'
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*host.py*' -and $_.Name -like 'python*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
Start-ScheduledTask -TaskName $taskName
$token = $null
try { $token = (Get-Content (Join-Path $dir 'config.json') -Raw | ConvertFrom-Json).token } catch {}
$ok = $false
for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Seconds 2
    try {
        $s = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/status' -Headers @{ 'X-Token' = $token } -TimeoutSec 5
        if ($s.ok) { $ok = $true; Write-Host "HEALTH_OK steam=$($s.steam) game=$($s.game)"; break }
    } catch {}
}
if ($ok) { Write-Host "RESTART_DONE" } else { Write-Host "HEALTH_STILL_FAILING"; Get-Content (Join-Path $dir 'host.log') -Tail 10 -ErrorAction SilentlyContinue }
