<#
.SYNOPSIS
  Backs up, restores and verifies a game's save folder around a test run.

.DESCRIPTION
  backup   copies the whole save folder to -To (default:
           <BackupRoot>\save-backup-<timestamp>) and prints the path.
  restore  copies a backup back over the save folder. Character folders
           (character_N) that exist now but not in the backup - the ones a test
           run created - are removed; nothing else is ever deleted.
  verify   hash-compares the save folder with a backup and prints every
           difference. Exit code 1 if there is any.

  Refuses to restore while the game is running: it would write the saves back
  on exit. Add a game to $Games below to support it.

.EXAMPLE
  $b = tools\game-saves.ps1 backup -Game Stoneshard
  tools\game-saves.ps1 restore -Game Stoneshard -From $b
  tools\game-saves.ps1 verify  -Game Stoneshard -From $b
#>
param(
    [Parameter(Mandatory, Position = 0)] [ValidateSet("backup", "restore", "verify")] [string] $Action,
    [string] $Game = "Stoneshard",
    [string] $From,
    [string] $To,
    [string] $BackupRoot = "D:\projects\coreloader-test",
    # Overrides the save folder from $Games (e.g. a copy, for trying the script out).
    [string] $SaveDir
)

$ErrorActionPreference = "Stop"

# SaveDir: the folder the game saves into. SlotParents: folders (relative to it)
# that hold per-character folders named SlotPattern, which restore may remove.
# Process: the exe name, to refuse a restore while the game runs.
$Games = @{
    Stoneshard = @{
        SaveDir     = Join-Path $env:LOCALAPPDATA "StoneShard"
        SlotParents = @(".", "characters_v1")
        SlotPattern = '^character_\d+$'
        Process     = "StoneShard"
    }
}

if (-not $Games.ContainsKey($Game)) { throw "unknown game '$Game' (known: $($Games.Keys -join ', '))" }
$cfg = $Games[$Game]
if (-not $SaveDir) { $SaveDir = $cfg.SaveDir }
$marker = "coreloader-backup.txt"

function Get-FileHashes([string] $root) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root)) { return $map }
    $base = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length)
        if ($rel -ne $marker) { $map[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    return $map
}

function Assert-Backup([string] $path) {
    if (-not $path) { throw "-From <backup folder> is required for $Action" }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "backup not found: $path" }
    $m = Join-Path $path $marker
    if (Test-Path -LiteralPath $m) {
        $owner = (Get-Content -LiteralPath $m -TotalCount 1).Trim()
        if ($owner -ne $Game) { throw "$path is a $owner backup, not $Game" }
    }
}

function Compare-Saves([string] $backup) {
    $want = Get-FileHashes $backup
    $have = Get-FileHashes $SaveDir
    $diffs = @()
    foreach ($k in $want.Keys | Sort-Object) {
        if (-not $have.ContainsKey($k)) { $diffs += "missing  $k" }
        elseif ($have[$k] -ne $want[$k]) { $diffs += "changed  $k" }
    }
    foreach ($k in $have.Keys | Sort-Object) {
        if (-not $want.ContainsKey($k)) { $diffs += "extra    $k" }
    }
    return , $diffs
}

switch ($Action) {
    "backup" {
        if (-not (Test-Path -LiteralPath $SaveDir)) { throw "save folder not found: $SaveDir" }
        if (-not $To) { $To = Join-Path $BackupRoot ("save-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
        if (Test-Path -LiteralPath $To) { throw "backup target already exists: $To" }
        New-Item -ItemType Directory -Force -Path $To | Out-Null
        Get-ChildItem -LiteralPath $SaveDir -Force |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $To -Recurse -Force }
        Set-Content -LiteralPath (Join-Path $To $marker) -Value $Game
        $n = (Get-ChildItem -LiteralPath $To -Recurse -File -Force).Count - 1
        Write-Host "backed up $n file(s) of $SaveDir"
        # The path alone on stdout, so callers can capture it.
        Write-Output $To
    }
    "restore" {
        Assert-Backup $From
        if (Get-Process -Name $cfg.Process -ErrorAction SilentlyContinue) {
            throw "$($cfg.Process) is running: close it first (it writes its saves on exit)"
        }
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        # Character folders a test run created. Only these are ever deleted.
        foreach ($parent in $cfg.SlotParents) {
            $live = Join-Path $SaveDir $parent
            if (-not (Test-Path -LiteralPath $live -PathType Container)) { continue }
            Get-ChildItem -LiteralPath $live -Directory -Force |
                Where-Object { $_.Name -match $cfg.SlotPattern } |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path (Join-Path $From $parent) $_.Name)) } |
                ForEach-Object {
                    Write-Host "removing $($_.FullName) (not in the backup)"
                    Remove-Item -LiteralPath $_.FullName -Recurse -Force
                }
        }
        Get-ChildItem -LiteralPath $From -Force |
            Where-Object { $_.Name -ne $marker } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $SaveDir -Recurse -Force }
        $diffs = Compare-Saves $From
        Write-Host "restored $From -> $SaveDir ($($diffs.Count) difference(s) left)"
        $diffs | ForEach-Object { Write-Host "  $_" }
        exit ([int]($diffs.Count -gt 0))
    }
    "verify" {
        Assert-Backup $From
        $diffs = Compare-Saves $From
        if ($diffs.Count -eq 0) { Write-Host "$SaveDir matches $From"; exit 0 }
        Write-Host "$SaveDir differs from $From in $($diffs.Count) file(s):"
        $diffs | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
}
