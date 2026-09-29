<#
.SYNOPSIS
  Exercises every StoneshardCheats test command against a running game and
  checks what it reads back. PASS/FAIL per check; exit code 1 on any FAIL.

.DESCRIPTION
  Needs Stoneshard started with the test host and StoneshardCheats deployed,
  with a save loaded and the character on the map:

    $b = tools\game-saves.ps1 backup -Game Stoneshard
    tools\run-game.ps1 -Game Stoneshard -TestHost -Deploy -Mods StoneshardCheats,Console -CleanMods
    (load a save)
    tools\smoke-stoneshard.ps1
    tools\run-game.ps1 -Game Stoneshard -Stop       # kill without saving
    tools\game-saves.ps1 verify -From $b

  The checks change the character (attributes, gold, XP, a status, a potion,
  items on the ground); values it can put back, it puts back. Never let the
  game save afterwards. -RemoveEnemy also destroys the nearest enemy.

.EXAMPLE
  tools\smoke-stoneshard.ps1 -Item "Militia Falchion" -RemoveEnemy
#>
param(
    [string] $Pipe,
    [string] $Item = "Militia Falchion",
    [string] $GiveObject = "o_inv_bottle",
    # A status object; default: the first beneficial one in the catalogue.
    [string] $Condition,
    # Potion effect tags; default: the first beneficial effect in the table.
    [string[]] $PotionTags,
    [switch] $RemoveEnemy
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "coreloader.ps1") -Game Stoneshard -Pipe $Pipe
Set-Alias cl Invoke-CoreLoader

$script:failed = 0
$script:passed = 0
function Check([string] $Name, [scriptblock] $Body) {
    # The block returns $true, or a string saying what was wrong.
    try { $r = & $Body } catch { $r = "error: $($_.Exception.Message)" }
    if ($r -eq $true) { $script:passed++; Write-Host "PASS  $Name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "FAIL  $Name  - $r" -ForegroundColor Red }
}

# ------------------------------------------------------------------ preflight

Check "ping" { (cl ping) -eq "pong" }
$status = cl status
Check "status: Stoneshard with the GML bridge proven" {
    if ($status.game -ne "StoneShard") { return "game is '$($status.game)'" }
    if (-not ($status.gmlReady -and $status.abiProven)) { return "gmlReady=$($status.gmlReady) abiProven=$($status.abiProven)" }
    $true
}
$cheats = $status.mods | Where-Object { $_.name -eq "Stoneshard Cheats" }
Check "StoneshardCheats running" { if ($cheats.state -eq "running") { $true } else { "state '$($cheats.state)' $($cheats.fault)" } }
Check "log readable" { $l = @(cl log 5); if ($l.Count -gt 0) { $true } else { "empty" } }

$player = Wait-CoreLoader { $p = cl cheats.player; if ($p.available) { $p } } -TimeoutSec 60 -What "the player (load a save)"
Write-Host "player at ($($player.x), $($player.y)), id $($player.id)"

# The catalogue reads the exe in the background after the mods start.
$null = Wait-CoreLoader { @(cl cheats.potion-effects).Count -gt 0 -and @(cl cheats.conditions).Count -gt 0 } -TimeoutSec 120 -What "the cheats catalogue"

# ---------------------------------------------------------------------- stats

Check "atr-set STR -> atr-get" {
    $orig = cl cheats.atr-get STR
    try {
        $set = cl cheats.atr-set STR ($orig + 1)
        $get = cl cheats.atr-get STR
        if ($set -ne $orig + 1 -or $get -ne $orig + 1) { return "was $orig, set answered $set, get answered $get" }
        $true
    }
    finally { $null = cl cheats.atr-set STR $orig }
}
Check "hp: restore does not lower HP" {
    $r = cl cheats.hp 10
    if ($r.after -ge $r.before) { $true } else { "HP $($r.before) -> $($r.after)" }
}
Check "gold +100 (scr_gold_count delta)" {
    $r = cl cheats.gold 100
    if ($null -eq $r.before -or $null -eq $r.after) { return "scr_gold_count unreadable (before $($r.before), after $($r.after))" }
    if ($r.after - $r.before -eq 100) { $true } else { "gold $($r.before) -> $($r.after)" }
}
Check "xp +10 raises XP or level" {
    $r = cl cheats.xp 10
    if ($r.after.xp -gt $r.before.xp -or $r.after.lvl -gt $r.before.lvl) { $true }
    else { "xp $($r.before.xp) -> $($r.after.xp), lvl $($r.before.lvl) -> $($r.after.lvl)" }
}

# ----------------------------------------------------------------------- body

Check "body-set -> Body map" {
    $body = cl cheats.body
    $part = @($body.PSObject.Properties.Name)[0]
    if (-not $part) { return "Body_Parts_map is empty" }
    $orig = $body.$part
    try {
        $after = cl cheats.body-set $part 50
        if ($after.$part -eq 50) { $true } else { "$part answered $($after.$part)" }
    }
    finally { $null = cl cheats.body-set $part $orig }
}

# --------------------------------------------------------------------- psyche

Check "psy-set -> psy-get" {
    $psy = cl cheats.psy-get
    $key = @($psy.PSObject.Properties | Where-Object { $_.Value -is [ValueType] -and $_.Value -isnot [bool] } | Select-Object -First 1).Name
    if (-not $key) { return "psyData has no numeric field" }
    $orig = $psy.$key
    try {
        $set = cl cheats.psy-set $key ($orig + 1)
        $get = cl cheats.psy-get $key
        if ($set -eq $orig + 1 -and $get -eq $orig + 1) { $true } else { "$key was $orig, set $set, get $get" }
    }
    finally { $null = cl cheats.psy-set $key $orig }
}

# ------------------------------------------------------------------ condition

if (-not $Condition) { $Condition = @(cl cheats.conditions | Where-Object { $_.positive })[0].name }
Check "condition $Condition -> buffs +1" {
    $r = cl cheats.condition $Condition 50
    if ($r.after -eq $r.before + 1) { $true } else { "buffs $($r.before) -> $($r.after)" }
}

# --------------------------------------------------------------------- potion

if (-not $PotionTags) { $PotionTags = @(@(cl cheats.potion-effects | Where-Object { $_.positive })[0].tag) }
Check "potion $($PotionTags -join ',') builds" {
    $armed = Invoke-CoreLoader cheats.potion @PotionTags
    $r = Wait-CoreLoader { $o = cl cheats.potion-result; if (-not $o.pending -and $o.seq -ge $armed.seq) { $o } } -TimeoutSec 10 -What "the potion build"
    if ($r.seq -ne $armed.seq) { return "outcome #$($r.seq), expected #$($armed.seq)" }
    if ($r.ok) { $true } else { $r.message }
}

# ---------------------------------------------------------------------- items

Check "item-give '$Item' returns a live instance" {
    $r = cl cheats.item-give $Item Rare 1
    if ($r.source -ne "gear") { return "'$Item' is an object item (source $($r.source)); pass a weapon or armor -Item" }
    $inst = @($r.instances)
    if ($inst.Count -ne 1) { return "$($inst.Count) instance(s)" }
    if (cl builtin instance_exists $inst[0]) { $true } else { "instance $($inst[0]) does not exist" }
}
Check "object-give $GiveObject -> one more instance" {
    $r = cl cheats.object-give $GiveObject
    if ($r.after -gt $r.before) { $true } else { "$GiveObject count $($r.before) -> $($r.after)" }
}

# -------------------------------------------------------------------- enemies

$enemies = @(cl cheats.enemies)
Check "enemies roster non-empty" { if ($enemies.Count -gt 0) { $true } else { "no enemies in this room (load a save with enemies nearby)" } }
if ($RemoveEnemy -and $enemies.Count -gt 0) {
    Check "enemy-remove $($enemies[0].name)" {
        $r = cl cheats.enemy-remove $enemies[0].key
        if ($r.gone -and $r.left -eq $enemies.Count - 1) { $true } else { "gone=$($r.gone), left $($r.left) of $($enemies.Count)" }
    }
}

Write-Host ""
Write-Host "$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
