<#
.SYNOPSIS
  Plays Stoneshard over the test host, through the StoneshardHarness mod.

.DESCRIPTION
  The game must run with the test host on and the harness deployed:
    tools\run-game.ps1 -Game Stoneshard -TestHost -Deploy -Mods StoneshardHarness

  Looking (compact tables; -Json prints the mod's JSON instead):
    state                 room, cell, turn, open menus, the player's vitals and gold
    player                position, vitals, weapon, worn items, hotbar, the 8 moves
    enemies [all]         hostile units in sight (all: every live one), nearest first
    npcs [all]            people and animals in sight
    objects [reach] [all] doors, containers, items, NPCs, corpses nearby
    inventory             the bag and worn items (screen pixels while it is open)
    actions <id>          an item's or object's context-menu actions (opens and closes the menu)
    log [n]               the last n action-log lines
    dialogue              the open conversation and its options
    screen <gx> <gy>      a grid cell as desktop pixels (also: screen room x y, screen at sx sy)
    buttons               window buttons on screen (CONFIRM, CANCEL...)

  Acting (each waits until the game has settled and prints what happened:
  turns taken, HP, where the player ended up, the target's HP, new log lines):
    move <dx> <dy>        one step
    goto <gx> <gy>        walk to a cell
    attack <id> [turns]   attack an enemy, once per turn (steps towards it first) until it dies or turns run out
    interact <id> [action]  talk / open / enter / pick up, or a context-menu action
    use <itemId> [action] an item's action (default: its first, e.g. Eat, Equip)
    wait [turns]          skip turns
    say <n|key|text>      pick a conversation option
    press <id|text>       press a window button (CONFIRM...)
    key <key>             a key through the game (i, esc, space, 1-9, f1...)
    click <sx> <sy> [right]   the fallback: a real click (game must be in front)
    close                 close a context menu, else press Esc

  Anything else is sent as it is (hx.<cmd> or any test-host command).
  Exit codes as tools\coreloader.ps1: 0 ok, 1 command failed, 2 unreachable, 3 no answer in time.

.EXAMPLE
  tools\stoneshard.ps1 state
  tools\stoneshard.ps1 enemies
  tools\stoneshard.ps1 attack 401234
  tools\stoneshard.ps1 use 399499 Use
  tools\stoneshard.ps1 -Json objects 20
#>
param(
    [switch] $Json,
    # How long an action may take before this gives up waiting (it keeps running in the game);
    # attack and wait get 15 s per turn asked for, goto at least 3 minutes.
    [int] $ActionTimeoutSec = 60,
    [Parameter(Position = 0)] [string] $Verb = "state",
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)] [object[]] $Rest = @()
)

$ErrorActionPreference = "Stop"
# Dot-sourcing coreloader.ps1 sets its own $Game; nothing here is named that.
. (Join-Path $PSScriptRoot "coreloader.ps1") -Game Stoneshard

$values = @($Rest | ForEach-Object { ConvertFrom-CliArgument $_ })

$Actions = @{
    move = "hx.move"; goto = "hx.goto"; attack = "hx.attack"; interact = "hx.interact"; use = "hx.use"
    wait = "hx.wait"; say = "hx.say"; key = "hx.key"; click = "hx.click"; actions = "hx.actions"; press = "hx.press"
}
$Looks = @{
    state = "hx.state"; player = "hx.player"; enemies = "hx.enemies"; npcs = "hx.npcs"; objects = "hx.objects"
    inventory = "hx.inventory"; log = "hx.log"; dialogue = "hx.dialogue"; screen = "hx.screen"; buttons = "hx.buttons"
    close = "hx.close"
}

function Show-Table($rows, [object[]] $columns) {
    if (-not $rows) { "(none)"; return }
    $rows | Select-Object -Property $columns | Format-Table -AutoSize | Out-String -Width 220 | ForEach-Object { $_.TrimEnd() }
}

function Format-Outcome($r) {
    $lines = @()
    $head = "#$($r.seq) $($r.kind) $($r.what)"
    if ($r.timedOut) { $head += " (TIMED OUT)" }
    if ($null -ne $r.turns) { $head += ": $($r.turns) turn(s)" }
    $lines += $head
    if ($r.player) {
        $p = $r.player
        $lines += "  player hp $($p.hp[0]) -> $($p.hp[1]), mp $($p.mp[0]) -> $($p.mp[1]), gold $($p.gold[0]) -> $($p.gold[1]), cell $($p.from.gx),$($p.from.gy) -> $($p.to.gx),$($p.to.gy)$(if (-not $p.sameInstance) { ' (new room/instance)' })"
    }
    else { $lines += "  no player now" }
    if ($r.target) {
        $t = $r.target
        $lines += "  target $($t.name) [$($t.id)] hp $($t.hpBefore) -> $($t.hpAfter)$(if ($t.dead) { ' DEAD' })"
    }
    if ($r.extra) { $lines += "  " + (ConvertTo-Json -InputObject $r.extra -Compress -Depth 6) }
    if ($r.error) { $lines += "  ERROR: $($r.error)" }
    if ($r.note) { $lines += "  note: $($r.note)" }
    if ($r.room) { $lines += "  room: $($r.room)" }
    if ($r.menus) { $lines += "  open: $($r.menus -join ', ')" }
    foreach ($l in $r.log) { $lines += "  log: $l" }
    $lines
}

try {
    if ($Actions.ContainsKey($Verb)) {
        $start = Invoke-CoreLoader $Actions[$Verb] @values
        $seq = [int]$start.seq
        # Long actions get longer: 15 s for each attack or wait turn asked for,
        # and at least three minutes for the walk of a goto.
        $limit = $ActionTimeoutSec
        $turnsArg = if ($Verb -eq "attack") { 1 } elseif ($Verb -eq "wait") { 0 } else { -1 }
        if ($turnsArg -ge 0 -and $values.Count -gt $turnsArg) { $limit = [Math]::Max($limit, 30 + 15 * [int]$values[$turnsArg]) }
        if ($Verb -eq "goto") { $limit = [Math]::Max($limit, 180) }
        # Polled here rather than with Wait-CoreLoader, which takes every error
        # for "not yet": a game that has gone must fail at once (exit 2), and
        # running out of time must exit 3.
        $deadline = (Get-Date).AddSeconds($limit)
        while ($true) {
            $r = Invoke-CoreLoader hx.result $seq
            if ($r.done) { break }
            if ((Get-Date) -gt $deadline) { throw [System.TimeoutException]::new("action $seq still running after $limit s (it goes on in the game; hx.result $seq)") }
            Start-Sleep -Milliseconds 200
        }
        if ($Json) { ConvertTo-Json -InputObject $r -Depth 10 }
        elseif ($Verb -eq "actions" -and $r.extra.actions) { Show-Table $r.extra.actions @('action', 'label') }
        else { Format-Outcome $r }
        exit $(if ($r.error) { 1 } else { 0 })
    }

    $cmd = if ($Looks.ContainsKey($Verb)) { $Looks[$Verb] } else { $Verb }
    $r = Invoke-CoreLoader $cmd @values
    if ($Json -or -not $Looks.ContainsKey($Verb)) {
        if ($null -eq $r -or $r -is [string] -or $r -is [ValueType]) { $r } else { ConvertTo-Json -InputObject $r -Depth 10 }
        exit 0
    }

    switch ($Verb) {
        "state" {
            $p = $r.player
            "room $(if ($r.room) { $r.room } else { '?' })  cell $($r.cell.x),$($r.cell.y)  floor $($r.floor)$(if ($r.dungeon) { ' (dungeon)' })  turn $($r.turn)  $(if ($r.playerTurn) { 'YOUR TURN' } else { 'busy' })"
            if ($p) { "$($p.name): hp $($p.hp)/$($p.maxHp)  mp $($p.mp)/$($p.maxMp)  lvl $($p.level)  xp $($p.xp)/$($p.maxXp)  at $($p.gx),$($p.gy) (screen $($p.sx),$($p.sy))  gold $($r.gold)" }
            else { "no player" }
            "open: $(if ($r.menus) { $r.menus -join ', ' } else { '-' })   enemies in sight: $($r.enemiesVisible)$(if ($r.busy) { '   (an action is running)' })"
        }
        "player" {
            $v = $r.vitals
            "$($r.name) [$($r.id)] $($r.obj) at $($r.pos.gx),$($r.pos.gy) screen $($r.pos.sx),$($r.pos.sy)  $(if ($r.turn) { 'YOUR TURN' } else { $r.state })"
            "hp $($v.hp)/$($v.maxHp)  mp $($v.mp)/$($v.maxMp)  lvl $($v.level) xp $($v.xp)  hunger $($v.hunger) thirst $($v.thirst) fatigue $($v.fatigue) pain $($v.pain) morale $($v.morale) sanity $($v.sanity)"
            "weapon: $(if ($r.weapon.ranged) { "$($r.weapon.name) (ranged $($r.weapon.range))" } else { 'melee' })"
            "moves: " + (($r.moves | ForEach-Object { if ($_.free) { $_.dir } else { "$($_.dir)x($($_.blocked))" } }) -join ' ')
            if ($r.hotbar) { "hotbar:"; Show-Table $r.hotbar @('key', 'name', 'cooldown', 'mp', 'ready', 'id') }
            "worn:"; Show-Table $r.equipped @('id', 'name', 'slot')
        }
        { $_ -in "enemies", "npcs" } {
            $cols = @('id', 'name', @{ n = 'hp'; e = { if ($null -ne $_.hp) { "$($_.hp)/$($_.maxHp)" } } }, 'dist',
                      @{ n = 'cell'; e = { "$($_.pos.gx),$($_.pos.gy)" } }, @{ n = 'screen'; e = { "$($_.pos.sx),$($_.pos.sy)" } })
            if ($Verb -eq "enemies") { $cols += @('aware', 'state', @{ n = 'can'; e = { $_.can -join ',' } }) } else { $cols += @('obj', 'hostile') }
            Show-Table $r $cols
        }
        "objects" {
            Show-Table $r @('id', 'kind', 'name', 'exit', 'dist', @{ n = 'cell'; e = { "$($_.pos.gx),$($_.pos.gy)" } },
                            @{ n = 'screen'; e = { "$($_.pos.sx),$($_.pos.sy)" } }, @{ n = 'actions'; e = { $_.actions -join ',' } }, 'obj')
        }
        "inventory" {
            "inventory $(if ($r.open) { 'OPEN' } else { 'closed' })  gold $($r.gold)"
            $cols = @('id', 'name', 'kind', 'stack', 'charges', 'equipped', 'slot', 'price', @{ n = 'screen'; e = { if ($_.screen) { "$($_.screen.x),$($_.screen.y)" } } })
            Show-Table $r.items $cols
            foreach ($w in $r.windows) { ""; "$($w.kind) window [$($w.id)]:"; Show-Table $w.items $cols }
        }
        "log" { $r }
        "buttons" { Show-Table $r @('id', 'text', 'enabled', @{ n = 'screen'; e = { if ($_.screen) { "$($_.screen.x),$($_.screen.y)" } } }, 'obj') }
        "dialogue" {
            if (-not $r.speaker) { "no conversation"; break }
            "$($r.speaker): $($r.text)"
            foreach ($o in $r.options) { "  [$($o.n)] $($o.text)   ($($o.key))$(if (-not $o.enabled) { ' - unavailable' })" }
        }
        default { ConvertTo-Json -InputObject $r -Depth 10 }
    }
    exit 0
}
catch {
    $ex = $_.Exception
    while ($ex.InnerException -and $ex -is [System.Management.Automation.MethodInvocationException]) { $ex = $ex.InnerException }
    [Console]::Error.WriteLine($ex.Message)
    exit $(if ($ex -is [System.TimeoutException]) { 3 } elseif ($ex -is [System.IO.IOException]) { 2 } else { 1 })
}
