# SpireRemote installer (Windows, per-user, no admin required)
# - installs Python deps for the current user
# - generates config.json with a random token (if missing)
# - registers an at-logon scheduled task that runs host.py
# - if the Tailscale CLI is present, exposes the host at https://<your-machine>.<your-tailnet>.ts.net/spire
# - prints the phone URL (with token) at the end
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
Write-Host "== SpireRemote install =="
Write-Host "Folder: $dir"

# --- Python ---
function Find-Python {
    $cands = @()
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) { $cands += ,@($py.Source, '-3') }
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python -and $python.Source -notlike '*WindowsApps*') { $cands += ,@($python.Source, $null) }
    Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | ForEach-Object { $cands += ,@($_.FullName, $null) }
    foreach ($c in $cands) {
        try {
            $exe = $c[0]; $pre = $c[1]
            if ($pre) { $v = & $exe $pre -c "import sys; print(sys.version_info.major*100+sys.version_info.minor)" 2>$null }
            else      { $v = & $exe    -c "import sys; print(sys.version_info.major*100+sys.version_info.minor)" 2>$null }
            if ([int]$v -ge 311) {
                $real = if ($pre) { & $exe $pre -c "import sys; print(sys.executable)" } else { $exe }
                return $real.Trim()
            }
        } catch {}
    }
    return $null
}
$pythonExe = Find-Python
if (-not $pythonExe) {
    Write-Host "Python 3.11+ was not found. Install it from https://www.python.org/downloads/ (check 'Add python.exe to PATH'), then run install.ps1 again."
    exit 1
}
$pythonwExe = Join-Path (Split-Path $pythonExe) 'pythonw.exe'
if (-not (Test-Path $pythonwExe)) { $pythonwExe = $pythonExe }
Write-Host "Python: $pythonExe"

# --- Dependencies ---
Write-Host "Installing Python packages (Pillow, windows-capture) for the current user..."
& $pythonExe -m pip install --user -r (Join-Path $dir 'requirements.txt')
if ($LASTEXITCODE -ne 0) { Write-Host "pip install failed."; exit 1 }

# --- Config / token ---
$configPath = Join-Path $dir 'config.json'
if (-not (Test-Path $configPath)) {
    $bytes = New-Object byte[] 20
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
    @{ token = $token } | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "Created config.json with a fresh token."
} else {
    Write-Host "config.json already exists; keeping your token."
}
$token = (Get-Content $configPath -Raw | ConvertFrom-Json).token

# --- Scheduled task (at logon, interactive session) ---
$taskName = 'SpireRemoteHost'
$action = New-ScheduledTaskAction -Execute $pythonwExe -Argument 'host.py' -WorkingDirectory $dir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Scheduled task '$taskName' registered and started."

# --- Health check ---
$ok = $false
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 2
    try {
        $s = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/status' -Headers @{ 'X-Token' = $token } -TimeoutSec 5
        if ($s.ok) { $ok = $true; break }
    } catch {}
}
if (-not $ok) {
    Write-Host "The host did not come up. Check host.log in $dir for errors."
    exit 1
}
Write-Host "Host is up on 127.0.0.1:8765."

# --- Tailscale Serve ---
$phoneUrl = $null
$ts = Get-Command tailscale -ErrorAction SilentlyContinue
if ($ts) {
    try {
        & tailscale serve --bg --set-path /spire 8765 | Out-Null
        $status = (& tailscale status --json | ConvertFrom-Json)
        $dns = $status.Self.DNSName.TrimEnd('.')
        $phoneUrl = "https://$dns/spire/?t=$token"
        Write-Host "Tailscale Serve configured for /spire."
    } catch {
        Write-Host "Tailscale Serve setup failed: $($_.Exception.Message)"
        Write-Host "Run manually:  tailscale serve --bg --set-path /spire 8765"
    }
} else {
    Write-Host "Tailscale CLI not found. Install Tailscale on this PC and on your phone, sign both into the SAME tailnet, then run:"
    Write-Host "  tailscale serve --bg --set-path /spire 8765"
}

Write-Host ""
Write-Host "================  OPEN THIS ON YOUR PHONE  ================"
if ($phoneUrl) { Write-Host $phoneUrl } else { Write-Host "https://<your-machine>.<your-tailnet>.ts.net/spire/?t=$token" }
Write-Host "==========================================================="
Write-Host "1) Make sure the Tailscale app on your phone is Connected (same account)."
Write-Host "2) Open the URL, then 'Add to Home Screen' to use it as an app."
Write-Host "3) In Slay the Spire 2, enable for the best experience:"
Write-Host "   Settings > Controls > Keyboard-Only Mode = ON"
Write-Host "   Settings > General  > Show Card Indices (hand numbers) = ON"
Write-Host "The token in the URL is a password for this remote. Do not share the URL."
