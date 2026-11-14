# Remove the SpireRemote scheduled task and the Tailscale Serve mapping.
# Your files (including config.json) are left in place; delete the folder yourself if you want a full removal.
$ErrorActionPreference = 'Continue'
$taskName = 'SpireRemoteHost'
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*host.py*' -and $_.Name -like 'python*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Get-Command tailscale -ErrorAction SilentlyContinue) {
    & tailscale serve --set-path /spire off 2>$null
}
Write-Host "SpireRemote task and Serve mapping removed."
