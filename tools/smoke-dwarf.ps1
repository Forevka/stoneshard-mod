<#
.SYNOPSIS
  Minimal test-host smoke test against Dwarf Eats Mountain (2024 runtime):
  the core commands and the Console mod's. PASS/FAIL per check; exit code 1
  on any FAIL.

.EXAMPLE
  tools\run-game.ps1 -Game Dwarf -TestHost -Deploy -Mods Console,DwarfBoost -CleanMods
  tools\smoke-dwarf.ps1
#>
param([string] $Pipe)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "coreloader.ps1") -Game Dwarf -Pipe $Pipe
Set-Alias cl Invoke-CoreLoader

$script:failed = 0
$script:passed = 0
function Check([string] $Name, [scriptblock] $Body) {
    try { $r = & $Body } catch { $r = "error: $($_.Exception.Message)" }
    if ($r -eq $true) { $script:passed++; Write-Host "PASS  $Name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "FAIL  $Name  - $r" -ForegroundColor Red }
}

Check "ping" { (cl ping) -eq "pong" }
Check "status: GML bridge proven, mods started" {
    $s = Wait-CoreLoader { $x = cl status; if ($x.modsStarted) { $x } } -TimeoutSec 60 -What "mods to start"
    if ($s.gmlReady -and $s.abiProven) { $true } else { "gmlReady=$($s.gmlReady) abiProven=$($s.abiProven)" }
}
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
Check "builtin sprite_exists 0" { (cl builtin sprite_exists 0) -eq $true -or (cl builtin sprite_exists 0) -eq 1 }
Check "global-set -> global-get" {
    $set = cl global-set __coreloader_smoke 42
    $get = cl global-get __coreloader_smoke
    if ($set -eq 42 -and $get -eq 42) { $true } else { "set $set, get $get" }
}
Check "object-count oSys" { $n = cl object-count oSys; if ($n -ge 1) { $true } else { "$n instances" } }
Check "instance-get oSys 0 gold is a number" {
    $g = cl instance-get oSys 0 gold
    if ($g -is [ValueType] -and $g -isnot [bool]) { $true } else { "gold = '$g'" }
}
Check "console 1 + 2" {
    $r = cl console "1 + 2"
    if ("$r".Trim() -eq "3") { $true } else { "console answered '$r'" }
}
Check "unknown command is refused" {
    $r = Invoke-CoreLoader no-such-command -Raw
    if (-not $r.ok -and $r.error -match "unknown command") { $true } else { "ok=$($r.ok) error=$($r.error)" }
}

Write-Host ""
Write-Host "$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
