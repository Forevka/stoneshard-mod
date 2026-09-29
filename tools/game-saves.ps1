<#
.SYNOPSIS
  Backs up, restores and verifies a game's save folder around a test run.

.DESCRIPTION
  backup   copies the whole save folder to -To (default:
           <BackupRoot>\save-backup-<timestamp>) and prints the path.
  restore  copies a backup back over the save folder. Character folders
           (character_N) that exist now but not in the backup - the ones a test
           run created - are removed, and so are files inside a backed-up
           character folder that the backup lacks, so each character comes
           back exactly. Nothing outside character folders is ever deleted.
  verify   hash-compares the save folder with a backup and prints every
           difference. Exit code 1 if there is any.

  Refuses to restore while the game is running: it would write the saves back
  on exit. Also refuses, unless -Force, a folder this script did not write (no
  coreloader-backup.txt marker), and a backup with no character folders while
  the save folder has some: both mean -From points at the wrong folder, and
  restoring it would delete every character. Add a game to $Games below to
  support it.

  Backups go under -BackupRoot, by default $env:CORELOADER_BACKUP_ROOT or else
  <repo>\.omc\save-backups (git-ignored).

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
    [string] $BackupRoot,
    # Overrides the save folder from $Games (e.g. a copy, for trying the script out).
    [string] $SaveDir,
    # Restores a folder without the backup marker, or with no character folders.
    [switch] $Force
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
if (-not $BackupRoot) {
    $BackupRoot = if ($env:CORELOADER_BACKUP_ROOT) { $env:CORELOADER_BACKUP_ROOT }
                  else { Join-Path (Split-Path -Parent $PSScriptRoot) ".omc\save-backups" }
}
$marker = "coreloader-backup.txt"

# The character folders directly under each slot parent of $root, as paths
# relative to $root (".\character_1", "characters_v1\character_2").
function Get-SlotFolders([string] $root) {
    foreach ($parent in $cfg.SlotParents) {
        $dir = Join-Path $root $parent
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $dir -Directory -Force |
            Where-Object { $_.Name -match $cfg.SlotPattern } |
            ForEach-Object { Join-Path $parent $_.Name }
    }
}

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
    # Verify only reads, so any folder can be compared; restore deletes, so it
    # takes only what backup wrote.
    elseif ($Action -eq "restore" -and -not $Force) {
        throw "$path has no $marker, so this script did not write it; check -From, or add -Force to restore it anyway"
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
        $liveSlots = @(Get-SlotFolders $SaveDir)
        $backupSlots = @(Get-SlotFolders $From)
        if ($backupSlots.Count -eq 0 -and $liveSlots.Count -gt 0 -and -not $Force) {
            throw "$From holds no character folders but $SaveDir has $($liveSlots.Count); restoring would delete them all. Check -From, or add -Force"
        }
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        # Character folders a test run created, and files a test run added
        # inside a backed-up character folder. Only these are ever deleted.
        foreach ($slot in $liveSlots) {
            $live = Join-Path $SaveDir $slot
            $saved = Join-Path $From $slot
            if (-not (Test-Path -LiteralPath $saved -PathType Container)) {
                Write-Host "removing $live (not in the backup)"
                Remove-Item -LiteralPath $live -Recurse -Force
                continue
            }
            $base = (Resolve-Path -LiteralPath $live).Path.TrimEnd('\') + '\'
            Get-ChildItem -LiteralPath $live -Recurse -File -Force |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path $saved $_.FullName.Substring($base.Length)) -PathType Leaf) } |
                ForEach-Object {
                    Write-Host "removing $($_.FullName) (not in the backup)"
                    Remove-Item -LiteralPath $_.FullName -Force
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
