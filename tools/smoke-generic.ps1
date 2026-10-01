<#
.SYNOPSIS
  Game-agnostic test-host smoke test: the core commands and the Console mod's,
  against any YYC game. It knows no object, script or variable names; it finds
  them in the running game (Console's 'objects', 'vars' and 'find'). PASS/FAIL
  per check; exit code 1 on any FAIL.

.EXAMPLE
  tools\run-game.ps1 -GameDir "D:\Games\Some Game" -TestHost -Deploy -Mods Console -CleanMods
  tools\smoke-generic.ps1 -GameDir "D:\Games\Some Game"
#>
param(
    [Parameter(Mandatory)] [string] $GameDir,
    [string] $Pipe
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "coreloader.ps1") -GameDir $GameDir -Pipe $Pipe
Set-Alias cl Invoke-CoreLoader

$script:failed = 0
$script:passed = 0
function Check([string] $Name, [scriptblock] $Body) {
    try { $r = & $Body } catch { $r = "error: $($_.Exception.Message)" }
    if ($r -eq $true) { $script:passed++; Write-Host "PASS  $Name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "FAIL  $Name  - $r" -ForegroundColor Red }
}
# Console output lines, without the blank ones.
function ConsoleLines([string] $Line) { @((@(cl console $Line) -join "`n") -split "`r?`n" | Where-Object { $_.Trim() }) }
# 'vars' output without the variables an earlier run of this script left behind.
function GameVars([string] $Object) { @(ConsoleLines "vars $Object" | Where-Object { $_ -notmatch '^\s+__coreloader' }) }

Check "ping" { (cl ping) -eq "pong" }
$status = $null
Check "status: GML bridge proven, mods started" {
    $script:status = Wait-CoreLoader { $x = cl status; if ($x.modsStarted) { $x } } -TimeoutSec 90 -What "mods to start"
    if ($status.gmlReady -and $status.abiProven) { $true } else { "gmlReady=$($status.gmlReady) abiProven=$($status.abiProven)" }
}
if ($status) { Write-Host "      game '$($status.game)'" }
Check "no faulted mods" {
    $bad = @(cl mods | Where-Object { $_.state -eq "faulted" })
    if ($bad.Count -eq 0) { $true } else { ($bad | ForEach-Object { "$($_.name): $($_.fault)" }) -join "; " }
}
Check "log readable" { @(cl log 5).Count -gt 0 }
Check "wait-frames 5 advances 5 frames" {
    $a = cl wait-frames 0
    $b = cl wait-frames 5
    if ($b - $a -ge 5) { $true } else { "frame $a -> $b" }
}
Check "builtin with a string argument and result" {
    $r = cl builtin string_upper "abc"
    if ($r -eq "ABC") { $true } else { "string_upper answered '$r'" }
}
Check "builtin with numbers" {
    $r = cl builtin max 3 9 4
    if ($r -eq 9) { $true } else { "max answered '$r'" }
}
Check "global-set -> global-get" {
    $set = cl global-set __coreloader_smoke 42
    $get = cl global-get __coreloader_smoke
    if ($set -eq 42 -and $get -eq 42) { $true } else { "set $set, get $get" }
}
Check "global-set string" {
    $get = cl global-set __coreloader_smoke_s "hello"
    if ($get -eq "hello") { $true } else { "read back '$get'" }
}

# ---- Console
Check "console 1 + 2" {
    $r = cl console "1 + 2"
    if ("$r".Trim() -eq "3") { $true } else { "console answered '$r'" }
}
Check "console builtin call with a string" {
    $r = cl console 'string_upper("abc") + "!"'
    if ("$r".Trim() -eq '"ABC!"') { $true } else { "console answered '$r'" }
}
Check "console global assign and read" {
    cl console "global.__coreloader_smoke = 7" | Out-Null
    cl console "global.__coreloader_smoke += 1" | Out-Null
    $r = cl console "global.__coreloader_smoke"
    if ("$r".Trim() -eq "8") { $true } else { "console answered '$r'" }
}
Check "console help" { (ConsoleLines "help").Count -gt 5 }
Check "console globals lists the smoke global" {
    $l = ConsoleLines "globals __coreloader"
    if ($l -match "__coreloader_smoke = 8") { $true } else { "got: $($l -join ' | ')" }
}

# An object with live instances, and one of its variables, found in the game.
$obj = $null; $var = $null
Check "console objects lists live instances" {
    $l = ConsoleLines "objects"
    $names = @($l | Where-Object { $_ -match '^\s+(\S+) x(\d+)$' } | ForEach-Object { $null = $_ -match '^\s+(\S+) x(\d+)$'; $Matches[1] })
    if ($names.Count -eq 0) { return "no object with instances: $($l -join ' | ')" }
    # Bullets and particles come and go between two commands; the checks below
    # need an instance that stays, so pick one whose first instance keeps its
    # id over a second, and that has variables of its own (some have none).
    $ids = @{}
    foreach ($n in $names) { try { $ids[$n] = cl instance-get $n 0 id } catch { } }
    cl wait-frames 60 | Out-Null
    $stable = @($names | Where-Object {
        try { $ids.ContainsKey($_) -and "$(cl instance-get $_ 0 id)" -eq "$($ids[$_])" } catch { $false } })
    $script:obj = $stable | Where-Object { @(GameVars $_).Count -gt 0 } | Select-Object -First 1
    if (-not $obj) { $script:obj = $stable | Select-Object -First 1 }
    if (-not $obj) { return "no instance lived for 60 frames among: $($names -join ', ')" }
    Write-Host "      $($names.Count) object(s) live; using $obj"
    $true
}
if ($obj) {
    Check "object-count $obj agrees with instance_number" {
        $n = cl object-count $obj
        $m = "$(cl console "instance_number($obj)")".Trim()
        if ($n -ge 1 -and "$n" -eq $m) { $true } else { "object-count $n, instance_number $m" }
    }
    Check "console vars $obj" {
        $l = GameVars $obj
        # A number-valued variable, so instance-set can round-trip it later.
        $num = $l | Where-Object { $_ -match '^\s+([A-Za-z_]\w*) = -?[\d.]+(E[+-]?\d+)?$' } | Select-Object -First 1
        if ($num) { $null = $num -match '^\s+([A-Za-z_]\w*) = '; $script:var = $Matches[1] }
        Write-Host "      $($l.Count) variable(s); number variable: $var"
        if ($l.Count -gt 0) { $true } else { "no variables" }
    }
    Check "console built-in instance variable: object_index" {
        $c = "$(cl console "$obj.object_index")".Trim()
        $i = cl instance-get $obj 0 object_index
        # Newer runtimes answer a typed reference ("ref N") where older ones answer N.
        if (("$c" -replace '^ref ', '') -eq ("$i" -replace '^ref ', '')) { $true }
        elseif ($i -is [ValueType] -and "$c" -match '(\d+)$' -and [double]$Matches[1] -eq [double]$i) { $true }
        else { "console $c, instance-get $i" }
    }
    Check "instance-set -> instance-get (own variable)" {
        $set = cl instance-set $obj 0 __coreloader_smoke 5
        $get = cl instance-get $obj 0 __coreloader_smoke
        $c = "$(cl console "$obj.__coreloader_smoke")".Trim()
        if ($set -eq 5 -and $get -eq 5 -and $c -eq "5") { $true } else { "set $set, get $get, console $c" }
    }
    if ($var) {
        Check "instance-get $obj.$var matches console" {
            $a = cl instance-get $obj 0 $var
            $b = "$(cl console "$obj.$var")".Trim()
            if ([double]$a -eq [double]$b) { $true } else { "instance-get $a, console $b" }
        }
    }
}

$event = $null
Check "console find lists events" {
    $l = ConsoleLines "find _Step_"
    $script:event = $l | Where-Object { $_ -match '^\s+(gml_Object_\S+_Step_0)$' } | ForEach-Object { $_.Trim() } | Select-Object -First 1
    if (-not $event) { $script:event = $l | Where-Object { $_ -match '^\s+(gml_Object_\S+)$' } | ForEach-Object { $_.Trim() } | Select-Object -First 1 }
    if ($l.Count -gt 1) { Write-Host "      using $event"; $true } else { "find answered: $($l -join ' | ')" }
}
Check "console find builtin" {
    $l = ConsoleLines "find string_upp"
    if ($l -match "builtin string_upper \(1 args\)") { $true } else { "got: $($l -join ' | ')" }
}
Check "console code of a builtin" {
    $l = ConsoleLines "code string_upper"
    if ($l -match "builtin string_upper: native code") { $true } else { "got: $($l -join ' | ')" }
}
if ($event) {
    Check "console code $event" {
        $l = ConsoleLines "code $event"
        if ($l[0] -match [regex]::Escape($event) -and $l -match "calls \(") { $true } else { "got: $($l -join ' | ')" }
    }
    # Only a running event proves the hook fires; a paused or inactive object
    # would not step, so the hook count is reported, not required.
    Check "console hook / hooks / unhook $event" {
        $h = ConsoleLines "hook $event"
        if (-not ($h -match "hooked")) { return "hook: $($h -join ' | ')" }
        cl wait-frames 10 | Out-Null
        $list = "$(cl console hooks)"
        $u = ConsoleLines "unhook all"
        $mods = @(cl mods | Where-Object { $_.state -eq "faulted" })
        if ($list -match [regex]::Escape($event) -and ($u -match "unhooked") -and $mods.Count -eq 0) { $true }
        else { "hooks '$list', unhook '$($u -join ' | ')', faulted $($mods.Count)" }
    }
}
Check "console reports an unknown function as an error" {
    $r = Invoke-CoreLoader console "no_such_function_xyz(1)" -Raw
    if (-not $r.ok) { $true } else { "ok=$($r.ok) result=$($r.result)" }
}
Check "console reports a game error, and stays alive" {
    $r = Invoke-CoreLoader console 'real("not a number")' -Raw
    $alive = "$(cl console "2 * 21")".Trim()
    if (-not $r.ok -and $alive -eq "42") { $true } else { "ok=$($r.ok) error=$($r.error); afterwards '$alive'" }
}
# A failed array access leaves the runtime's sticky array-error byte set; the
# loader must clear it, or the game's next array access that tests it raises
# the game's own modal error box with the stale index (the test host then
# stops answering). The game is left to run its own array code for a while
# before a legitimate array_set proves the byte is clear.
Check "failed array_get leaves no stale runtime error" {
    # Where the loader could not find the flag it cannot clear it: this game is
    # then exposed to the original bug, which the check reports as such.
    if (@(cl log 5000) -match "array error flag not found") { return "the loader did not find the runtime's array error flag in this game" }
    $r = Invoke-CoreLoader console 'array_get(array_create(6), 6)' -Raw
    if ($r.ok) { return "the out-of-range array_get did not fail: $($r.result)" }
    $cleared = @(cl log 200) -match "cleared the runtime's array error flag"
    cl wait-frames 300 -TimeoutSec 30 | Out-Null
    $set = Invoke-CoreLoader console 'array_set(array_create(3), 0, 1)' -Raw -TimeoutSec 15
    if (-not $set.ok) { return "a legitimate array_set afterwards failed: $($set.error)" }
    if (-not $cleared) { return "no 'cleared the runtime's array error flag' line in the log" }
    $true
}
Check "unknown command is refused" {
    $r = Invoke-CoreLoader no-such-command -Raw
    if (-not $r.ok -and $r.error -match "unknown command") { $true } else { "ok=$($r.ok) error=$($r.error)" }
}
Check "still no faulted mods" {
    $bad = @(cl mods | Where-Object { $_.state -eq "faulted" })
    if ($bad.Count -eq 0) { $true } else { ($bad | ForEach-Object { "$($_.name): $($_.fault)" }) -join "; " }
}

Write-Host ""
Write-Host "$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
