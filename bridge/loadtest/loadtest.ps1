# Prepared only: do not execute during the current play session.
# Never starts/stops/focuses the game and never sends /do or input requests.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Deploy', 'Verify')]
    [string]$Phase,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$BridgeUrl = 'http://127.0.0.1:8766'
$SourceDir = 'C:\Users\nekom\SpireBridge\dist\SpireBridge'

function Test-GameStopped {
    # Fail closed if process enumeration itself fails. No process handles are modified.
    $running = @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -ieq 'SlayTheSpire2' })
    if ($running.Count -gt 0) {
        Write-Host 'BLOCKED: SlayTheSpire2.exe is running. No deployment performed.' -ForegroundColor Yellow
        return $false
    }
    return $true
}

function Invoke-Deploy {
    if (-not (Test-GameStopped)) { return }
    $gameRoot = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'SlayTheSpire2.exe') -PathType Leaf)) {
        throw 'GameDir does not contain SlayTheSpire2.exe. Nothing copied.'
    }
    $modsRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot 'mods'))
    $destination = [IO.Path]::GetFullPath((Join-Path $modsRoot 'SpireBridge'))
    if (-not $destination.StartsWith($modsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Destination escaped the game mods directory.'
    }
    # Reject junctions/symlinks that could redirect writes outside the intended destination.
    foreach ($directory in @($gameRoot, $modsRoot, $destination)) {
        if (Test-Path -LiteralPath $directory) {
            $item = Get-Item -LiteralPath $directory -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Deployment directory is a reparse point: $directory"
            }
        }
    }
    $files = @('SpireBridge.dll', 'SpireBridge.json')
    $hashes = @{}
    foreach ($name in $files) {
        $source = Join-Path $SourceDir $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source: $source" }
        $hashes[$name] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $target = Join-Path $destination $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Deployment target is a reparse point: $target"
            }
        }
    }
    if (-not (Test-GameStopped)) { return }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($name in $files) {
        if (-not (Test-GameStopped)) { return }
        $source = Join-Path $SourceDir $name
        $target = Join-Path $destination $name
        Copy-Item -LiteralPath $source -Destination $target -Force
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $hashes[$name] -or $sourceHash -ne $targetHash) {
            throw "FAIL SHA256: $name (source changed or destination mismatch)"
        }
        Write-Host "PASS SHA256 $name : $targetHash" -ForegroundColor Green
    }
    Write-Host "Deployment verified: $destination"
    Write-Host 'Start the game manually only after deployment has finished.'
}

function Save-Json {
    param([string]$Name, [object]$Value)
    $path = Join-Path $script:ResultsDir ($Name + '.json')
    $json = ConvertTo-Json -InputObject $Value -Depth 40
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

function Write-Result {
    param([string]$Step, [string]$Status, [string]$Detail)
    $color = if ($Status -eq 'PASS') { 'Green' } elseif ($Status -eq 'FAIL') { 'Red' } else { 'Yellow' }
    Write-Host ("{0} {1}: {2}" -f $Status, $Step, $Detail) -ForegroundColor $color
    $script:Results.Add([pscustomobject]@{ step = $Step; status = $Status; detail = $Detail })
    Save-Json -Name 'summary' -Value @($script:Results.ToArray())
}

function Get-BridgeJson {
    param([ValidateSet('health', 'state')][string]$Endpoint, [int]$TimeoutSec = 6)
    Invoke-RestMethod -Uri ($BridgeUrl + '/' + $Endpoint) -Method Get -TimeoutSec $TimeoutSec
}

function Confirm-Visual {
    param([string]$Step, [string]$Prompt)
    $answer = Read-Host ($Prompt + ' [PASS / FAIL / SKIP]')
    $status = $answer.Trim().ToUpperInvariant()
    if ($status -notin @('PASS', 'FAIL', 'SKIP')) { $status = 'SKIP' }
    Write-Result -Step ($Step + '-visual') -Status $status -Detail $Prompt
}

function Capture-Step {
    param([string]$Name, [string]$Instruction, [string[]]$Screens, [string]$Visual)
    Write-Host "`n$Name : $Instruction"
    $answer = Read-Host 'Prepare this screen manually, then Enter to capture (type SKIP to skip)'
    if ($answer.Trim() -ieq 'SKIP') {
        Write-Result -Step $Name -Status 'SKIP' -Detail 'Operator skipped this screen.'
        return
    }
    try {
        $health = Get-BridgeJson -Endpoint health
        Save-Json -Name ($Name + '-health') -Value $health
        $state = Get-BridgeJson -Endpoint state
        Save-Json -Name ($Name + '-state') -Value $state
        $healthOk = ($health.ok -eq $true -and $health.service -eq 'SpireBridge')
        $screenOk = ($state.screen -in $Screens)
        $shapeOk = @(@('screen', 'run', 'player', 'hand', 'enemies', 'potions', 'choices') |
            Where-Object { $_ -notin $state.PSObject.Properties.Name }).Count -eq 0
        $status = if ($healthOk -and $screenOk -and $shapeOk) { 'PASS' } else { 'FAIL' }
        Write-Result -Step $Name -Status $status -Detail ("health=$healthOk; schema=$shapeOk; screen={0}; expected={1}" -f $state.screen, ($Screens -join '/'))
        if ($state.screen -eq 'unknown') {
            Write-Host 'unknown is an unavailable-state fallback, not successful live state acquisition.' -ForegroundColor Yellow
        }
    } catch {
        Save-Json -Name ($Name + '-error') -Value @{ ok = $false; error = $_.Exception.Message }
        Write-Result -Step $Name -Status 'FAIL' -Detail $_.Exception.Message
    }
    Confirm-Visual -Step $Name -Prompt $Visual
}

function Invoke-Verify {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $script:ResultsDir = Join-Path $PSScriptRoot ('results\' + $stamp)
    $script:Results = [Collections.Generic.List[object]]::new()
    New-Item -ItemType Directory -Path $script:ResultsDir -Force | Out-Null
    Write-Host "Read-only bridge verification. JSON results: $script:ResultsDir"
    Write-Host 'All game startup, navigation, card/potion actions and shutdown are manual.'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $health = $null
    while ($watch.Elapsed.TotalSeconds -lt 120) {
        $remaining = 120 - $watch.Elapsed.TotalSeconds
        if ($remaining -lt 1) { break }
        try {
            $health = Get-BridgeJson -Endpoint health -TimeoutSec ([Math]::Max(1, [Math]::Min(4, [Math]::Floor($remaining))))
            break
        } catch {
            $pause = [int][Math]::Max(0, [Math]::Min(1000, (120 - $watch.Elapsed.TotalSeconds) * 1000))
            if ($pause -gt 0) { Start-Sleep -Milliseconds $pause }
        }
    }
    $watch.Stop()
    if ($null -eq $health) {
        Save-Json -Name '01-load-error' -Value @{ ok = $false; error = 'health_timeout'; elapsed_seconds = $watch.Elapsed.TotalSeconds }
        Write-Result -Step '01-load' -Status 'FAIL' -Detail 'No /health response within the 120-second wait budget. No game operation performed.'
        return
    }
    Save-Json -Name '01-load-health' -Value $health
    $validHealth = $false
    try { $validHealth = ($health.ok -eq $true -and $health.service -eq 'SpireBridge' -and $health.version -eq '0.2.0' -and $health.game_version -eq 'v0.113.1') } catch { }
    $healthStatus = if ($validHealth) { 'PASS' } else { 'FAIL' }
    Write-Result -Step '01-load' -Status $healthStatus -Detail 'Expected SpireBridge 0.2.0, game_version v0.113.1 (DESIGN.md baseline); inspect saved JSON on version mismatch.'
    try { Save-Json -Name '01-load-state' -Value (Get-BridgeJson -Endpoint state) }
    catch { Write-Result -Step '01-load-state' -Status 'FAIL' -Detail $_.Exception.Message }
    Confirm-Visual -Step '01-load' -Prompt 'Normal startup and [SpireBridge] Ready in godot.log; state is not stuck unknown'
    Capture-Step '02-menu' 'Stay at the main menu.' @('main_menu') 'Visible menu agrees with screen; run/player null and arrays empty'
    Capture-Step '03-map' 'Start a run manually and open the map. Capture before choosing a node.' @('map') 'Act/floor/gold and choices match map nodes; manually choose a current /actions ID and verify movement'
    Capture-Step '04-combat' 'Enter combat manually; capture before playing cards.' @('combat') 'HP/block/energy, all card costs/descriptions and enemy intent/damage agree; manually verify targeted + untargeted play and end_turn, then inspect enqueue/fallback logs'
    Capture-Step '05-potion-combat-before' 'Prepare an in-combat potion; capture before using it manually.' @('combat') 'Potion slot/name and valid targets agree with current /actions'
    Capture-Step '05-potion-combat-after' 'Use the potion manually, then capture.' @('combat') 'Potion is consumed and effect/target matches the game'
    Capture-Step '06-rewards' 'Finish combat manually; capture rewards before claiming.' @('rewards') 'Choices match rewards (record reflection failure if empty); manually claim and check card-selection transition'
    Capture-Step '06-card-selection' 'If a card reward opens card selection, capture; otherwise SKIP. Select the card manually afterward.' @('card_selection') 'Card selection screen is detected; selection actions are unsupported in v0.2.0, finish manually'
    Capture-Step '07-event-before' 'Reach an event manually and capture before choosing.' @('event') 'Choice labels/locks match the visible event; use only a currently enumerated action'
    Capture-Step '07-event-after' 'Choose an event option manually, then capture.' @('event', 'map', 'combat', 'rewards') 'The selected option advanced the event correctly'
    Capture-Step '07-rest-before' 'Reach a rest site manually; capture before choosing.' @('rest') 'Rest choice labels/enabled state match; use only a currently enumerated action'
    Capture-Step '07-rest-after' 'Choose a rest option manually, then capture.' @('rest', 'map', 'card_selection') 'The selected rest action took effect; finish unsupported card selection manually'
    Capture-Step '07-potion-outside-before' 'Prepare a usable out-of-combat potion if available; otherwise SKIP.' @('map', 'event', 'rest', 'merchant', 'treasure') 'Outside-combat potion and available action agree with the game'
    Capture-Step '07-potion-outside-after' 'Use that potion manually, then capture; SKIP if no applicable potion.' @('map', 'event', 'rest', 'merchant', 'treasure') 'Potion consumption/effect agrees outside combat'
    Capture-Step '08-before-exit' 'Capture the final screen before normal manual shutdown.' @('main_menu', 'map', 'combat', 'event', 'rest', 'merchant', 'treasure', 'rewards', 'card_selection') 'Final snapshot is consistent; preserve relevant bridge/game logs'
    $null = Read-Host 'Quit the game normally yourself, wait for it to exit, then press Enter'
    try {
        $afterHealth = Get-BridgeJson -Endpoint health -TimeoutSec 3
        Save-Json -Name '08-after-exit-health' -Value $afterHealth
        Write-Result -Step '08-exit' -Status 'FAIL' -Detail 'Bridge still responds after reported shutdown. Check for an independent harness; do not stop processes automatically.'
    } catch {
        Save-Json -Name '08-after-exit-health' -Value @{ reachable = $false; error = $_.Exception.Message }
        Write-Result -Step '08-exit' -Status 'PASS' -Detail 'Bridge no longer responds after operator-reported shutdown (endpoint evidence only).'
    }
    try { Save-Json -Name '08-after-exit-state' -Value (Get-BridgeJson -Endpoint state -TimeoutSec 3) }
    catch { Save-Json -Name '08-after-exit-state' -Value @{ reachable = $false; error = $_.Exception.Message } }
    Confirm-Visual -Step '08-exit' -Prompt 'Game has exited normally and godot.log has no abnormal bridge/shutdown errors'
    Write-Host "Finished. Review summary.json and all state snapshots in $script:ResultsDir"
}

if ($Phase -eq 'Deploy') { Invoke-Deploy } else { Invoke-Verify }
