<#
.SYNOPSIS
  Restarts a test game with CoreLoader and waits until its mods are loaded.

.DESCRIPTION
  Stops a running instance of the game, optionally deploys CoreLoader and mods
  (tools\deploy-coreloader.ps1), launches the game, and waits for this run's
  loader log (<game>\CoreLoader\Logs\coreloader.log) to report "mod(s)
  loaded" - and, with -WaitFor, a line matching that regex too. Then prints
  the log lines that matter: loaded mods, faults, errors and -WaitFor matches.
  Exit code 1 on timeout. -Stop only stops the game.

  -TestHost launches the game with CORELOADER_TEST=1, which turns on the
  loader's test host (the pipe tools\coreloader.ps1 talks to), and waits for
  its "test host ON" line as well. It also writes CoreLoader\testhost.enable,
  which does the same for games Steam relaunches (losing the variable); a
  launch without -TestHost, or -Stop, removes that file.

  The log counts as this run's once it names the pid of a live instance of
  the game ("loaded into pid N"), so a leftover log from the previous run, or
  a relaunch through Steam, cannot fool it.

.EXAMPLE
  tools\run-game.ps1 -Game Dwarf -Deploy -Mods Console,DwarfBoost -CleanMods
  tools\run-game.ps1 -Game Stoneshard -WaitFor 'StructProbe.*PASSED' -TimeoutSec 180
  tools\run-game.ps1 -Game Stoneshard -Stop
  tools\run-game.ps1 -Game Stoneshard -TestHost   # then: tools\coreloader.ps1 -Game Stoneshard ping
#>
param(
    [ValidateSet("Stoneshard", "Dwarf")] [string] $Game,
    # Any other game: its folder, and -Exe if it holds more than one exe.
    [string]   $GameDir,
    [string]   $Exe,
    [switch]   $Stop,
    [switch]   $Deploy,
    [string[]] $Mods = @(),
    [switch]   $CleanMods,
    [string]   $Configuration = "Release",
    [string]   $WaitFor,
    [switch]   $TestHost,
    [int]      $TimeoutSec = 120
)

$ErrorActionPreference = "Stop"

$Known = @{
    Stoneshard = @{ Dir = "D:\torrent\Stoneshard (Early Access)\Stoneshard";         Exe = "StoneShard.exe" }
    Dwarf      = @{ Dir = "D:\SteamLibrary\steamapps\common\Dwarf Eats Mountain Demo"; Exe = "Dwarf Eats Mountain.exe" }
}

if ($Game) {
    if (-not $GameDir) { $GameDir = $Known[$Game].Dir }
    if (-not $Exe)     { $Exe     = $Known[$Game].Exe }
}
if (-not $GameDir) { throw "pass -Game Stoneshard|Dwarf, or -GameDir" }
if (-not (Test-Path -LiteralPath $GameDir)) { throw "game folder not found: $GameDir" }
if (-not $Exe) {
    $exes = @(Get-ChildItem -LiteralPath $GameDir -Filter *.exe | Where-Object { $_.Name -notmatch 'unins|crash|redist' })
    if ($exes.Count -ne 1) { throw "cannot tell the game exe in $GameDir; pass -Exe" }
    $Exe = $exes[0].Name
}
$exePath  = Join-Path $GameDir $Exe
$procName = [IO.Path]::GetFileNameWithoutExtension($Exe)
$log      = Join-Path $GameDir "CoreLoader\Logs\coreloader.log"

function Stop-Game {
    $running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    Write-Host "stopping $procName (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force
    # It holds version.dll until it is really gone; a deploy would fail before.
    $running | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
}

Stop-Game
$marker = Join-Path $GameDir "CoreLoader\testhost.enable"
if ($Stop) {
    # A game started later from Steam must not come up with the test host on.
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
    exit 0
}

if ($Deploy) {
    $deployArgs = @{ GameDir = $GameDir; Mods = $Mods; Configuration = $Configuration }
    if ($CleanMods) { $deployArgs.CleanMods = $true }
    & (Join-Path $PSScriptRoot "deploy-coreloader.ps1") @deployArgs
}

# Steam relaunches some games through steam.exe, and the variable below does
# not survive that; the loader also turns the host on for this marker file.
# A launch without -TestHost (or -Stop) removes it, so the host does not stay on.
if ($TestHost) {
    New-Item -ItemType Directory -Force -Path (Split-Path $marker) | Out-Null
    Set-Content -LiteralPath $marker -Value "written by tools\run-game.ps1 -TestHost; a launch without it removes this file"
} elseif (Test-Path -LiteralPath $marker) {
    Remove-Item -LiteralPath $marker
}

$started = Get-Date
Write-Host "launching $exePath$(if ($TestHost) { ' with CORELOADER_TEST=1' })"
# The game inherits this process's environment. The variable is put back at
# once, so nothing else started from this shell runs a test host.
$previousTest = $env:CORELOADER_TEST
if ($TestHost) { $env:CORELOADER_TEST = "1" }
try { Start-Process -FilePath $exePath -WorkingDirectory $GameDir | Out-Null }
finally { $env:CORELOADER_TEST = $previousTest }

function Read-FreshLog {
    # The loader rotates the log at startup, so it can vanish between checks;
    # any failure just means "not yet".
    try {
        if ([IO.File]::GetLastWriteTime($log) -lt $started) { return $null }
        # Shared read: the game keeps the log open for writing.
        $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite, Delete')
        try { $text = [IO.StreamReader]::new($fs).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $null }
    $lines = $text -split "`r?`n"
    $pidLine = $lines | Where-Object { $_ -match 'loaded into pid (\d+)' } | Select-Object -First 1
    if (-not $pidLine) { return $null }
    $null = $pidLine -match 'loaded into pid (\d+)'
    $p = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
    if (-not $p -or $p.ProcessName -ne $procName) { return $null }
    return , $lines
}

$deadline = $started.AddSeconds($TimeoutSec)
$lines = $null
$loaded = $false
$matched = -not $WaitFor
while ((Get-Date) -lt $deadline) {
    $lines = Read-FreshLog
    if ($lines) {
        $loaded  = [bool]($lines | Where-Object { $_ -match 'mod\(s\) loaded' }) -and
                   (-not $TestHost -or [bool]($lines | Where-Object { $_ -match 'test host ON' }))
        if ($WaitFor) { $matched = [bool]($lines | Where-Object { $_ -match $WaitFor }) }
        if ($loaded -and $matched) { break }
    }
    if (-not (Get-Process -Name $procName -ErrorAction SilentlyContinue) -and ((Get-Date) - $started).TotalSeconds -gt 20) {
        Write-Host "$procName is not running any more"
        break
    }
    Start-Sleep -Milliseconds 500
}

$pattern = 'mod\(s\) loaded|\] loaded |fault|error|exception|failed|test host'
if ($WaitFor) { $pattern = "$pattern|$WaitFor" }
if ($lines) { $lines | Where-Object { $_ -match $pattern } | ForEach-Object { Write-Host $_ } }

$elapsed = [int]((Get-Date) - $started).TotalSeconds
if ($loaded -and $matched) {
    Write-Host "ready after $elapsed s ($log)"
    exit 0
}
$what = if (-not $loaded) { "'mod(s) loaded'" + $(if ($TestHost) { " and 'test host ON'" } else { "" }) } else { "/$WaitFor/" }
Write-Host "timed out after $elapsed s waiting for $what in $log"
if ($lines) { Write-Host "--- last lines ---"; $lines | Select-Object -Last 15 | ForEach-Object { Write-Host $_ } }
exit 1
