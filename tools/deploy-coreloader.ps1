<#
.SYNOPSIS
  Installs Lodestone (and optionally mods) into a YYC GameMaker game folder.

.DESCRIPTION
  Copies build\version.dll next to the game exe, the managed runtime into
  <game>\Lodestone\, and the named mods from managed\bin\<Config>\Mods (or
  TestMods) into <game>\Mods\. An existing version.dll that is not ours is
  kept as version.dll.bak the first time. Works while the game is closed; a
  running game locks version.dll and the runtime (use -Live then).

  -Live installs into a RUNNING game. Windows will not let a loaded DLL be
  overwritten, but it will let it be RENAMED - the lock is on the path, not the
  bytes. So each locked file is moved aside to <name>.old (or, while an earlier
  .old is itself still locked, <name>.<timestamp>.old) and the new build takes
  its name. Stale .old files are deleted best-effort on every run. The running
  process keeps executing the renamed image; the next launch picks up the new
  one. It does not hot-reload the loader: a restart is still needed to run the
  new native code or runtime. Mods are different: the running runtime reloads
  a mod dll the moment it lands in Mods\, with or without -Live.

  The runtime is installed before version.dll, so a failure part-way never
  leaves a new version.dll beside the old CoreLoader.dll.

  An install from before the rename (a CoreLoader\ folder) is moved to
  Lodestone\ first, so its generated interop, logs and save backups carry over.

.EXAMPLE
  tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Mods Console,ScriptSpy

.EXAMPLE
  tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Live
#>
param(
    [Parameter(Mandatory)] [string]   $GameDir,
    [string[]] $Mods = @(),
    [string]   $Configuration = "Release",
    [switch]   $CleanMods,
    [switch]   $Live
)

$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $PSScriptRoot
$native  = Join-Path $root "build\version.dll"
$managed = Join-Path $root "managed\bin\$Configuration"

if (-not (Test-Path -LiteralPath $GameDir)) { throw "game folder not found: $GameDir" }
if (-not (Test-Path -LiteralPath $native))  { throw "build\version.dll not found - build the native loader first" }
if (-not (Test-Path -LiteralPath (Join-Path $managed "CoreLoader\CoreLoader.dll"))) {
    throw "CoreLoader.dll not found - run: dotnet build managed\CoreLoader.sln -c $Configuration"
}

# Copies one file. With -Live, a destination locked by the running game is
# moved aside first. An earlier .old is only deletable once the process that
# had it loaded has exited, so failing to delete one is expected; the locked
# file then goes to a unique timestamped .old instead.
function Install-File([string] $Source, [string] $Destination) {
    # Every run, live or not: once the game that held them has exited, the
    # files an earlier -Live deploy moved aside are just clutter.
    $dir  = Split-Path -Parent $Destination
    $leaf = Split-Path -Leaf $Destination
    Get-ChildItem -LiteralPath $dir -Filter "$leaf*.old" -File -ErrorAction SilentlyContinue |
        ForEach-Object { $f = $_; try { Remove-Item -LiteralPath $f.FullName -Force } catch { Write-Host "note: $($f.Name) still in use, leaving it" } }
    if (-not $Live) {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        return
    }
    if (Test-Path -LiteralPath $Destination) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force   # fast path: nothing has it open
            return
        } catch {
            $old = "$Destination.old"
            if (Test-Path -LiteralPath $old) { $old = "$Destination.$(Get-Date -Format yyyyMMddHHmmssfff).old" }
            Move-Item -LiteralPath $Destination -Destination $old             # locked: move the live one aside
            Write-Host "in use: moved $leaf aside to $(Split-Path -Leaf $old)"
        }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$target    = Join-Path $GameDir "version.dll"
$backup    = Join-Path $GameDir "version.dll.bak"
$loaderDir = Join-Path $GameDir "Lodestone"
$runtime   = @(Get-ChildItem -LiteralPath (Join-Path $managed "CoreLoader") -File)

# The install folder used to be CoreLoader\. Moving it keeps what the loader
# wrote there; leaving it would strand that data beside a fresh, empty install.
$legacyDir = Join-Path $GameDir "CoreLoader"
if (Test-Path -LiteralPath (Join-Path $legacyDir "CoreLoader.dll")) {
    if (-not (Test-Path -LiteralPath $loaderDir)) {
        try { Move-Item -LiteralPath $legacyDir -Destination $loaderDir }
        catch { throw "could not move $legacyDir to $loaderDir (close the game once, then deploy again): $_" }
    } else {
        # Both exist (run-game.ps1 -TestHost writes its marker into Lodestone\
        # before any deploy): merge, keeping whatever Lodestone\ already has.
        & robocopy $legacyDir $loaderDir /E /MOVE /XC /XN /XO /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "could not merge $legacyDir into $loaderDir (close the game once, then deploy again)" }
        $global:LASTEXITCODE = 0   # 1-7 are robocopy's successes; callers read non-zero as failure
        Remove-Item -LiteralPath $legacyDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $legacyDir) { throw "could not remove $legacyDir (close the game once, then deploy again)" }
    }
    Write-Host "moved the old CoreLoader folder to Lodestone"
}

# Without -Live, fail before copying anything if the game holds the loader
# open, rather than half-way through with a mix of old and new files.
if (-not $Live) {
    foreach ($p in @($target) + @($runtime | ForEach-Object { Join-Path $loaderDir $_.Name })) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        try { [IO.File]::Open($p, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { throw "in use: $p - close the game first, or pass -Live" }
    }
}

# A Lodestone folder means the version.dll there is already ours; only a
# foreign one (another mod loader, an older build of this mod) is worth keeping.
$ours = Test-Path -LiteralPath (Join-Path $loaderDir "CoreLoader.dll")
if ((Test-Path -LiteralPath $target) -and -not $ours -and -not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup
    Write-Host "kept the previous version.dll as version.dll.bak"
}

# The runtime goes in before version.dll: if a copy fails part-way, the old
# version.dll is still in place, and a new version.dll never meets the old
# runtime (the CoreApi version check would refuse it, but the game would lose
# its mods until the next deploy).
New-Item -ItemType Directory -Force -Path $loaderDir | Out-Null
$runtime | ForEach-Object { Install-File $_.FullName (Join-Path $loaderDir $_.Name) }
# The mod analyzer is for compilers only (template mods reference it from
# here); the game never loads anything from this folder, but an IDE may.
$analyzer = Join-Path $managed "CoreLoader\Analyzers\CoreLoader.Analyzers.dll"
if (Test-Path -LiteralPath $analyzer) {
    $analyzerDir = Join-Path $loaderDir "Analyzers"
    New-Item -ItemType Directory -Force -Path $analyzerDir | Out-Null
    Install-File $analyzer (Join-Path $analyzerDir "CoreLoader.Analyzers.dll")
}
Install-File $native $target

$modsDir = Join-Path $GameDir "Mods"
New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
if ($CleanMods) {
    Get-ChildItem -LiteralPath $modsDir -Filter *.dll | Remove-Item -Force
    Get-ChildItem -LiteralPath $modsDir -Filter *.pdb | Remove-Item -Force
}

# `powershell -File ... -Mods A,B` hands over "A,B" as ONE string; split it.
$Mods = @($Mods | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

foreach ($m in $Mods) {
    $found = $null
    foreach ($sub in "Mods", "TestMods") {
        $p = Join-Path $managed "$sub\$m.dll"
        if (Test-Path -LiteralPath $p) { $found = $p; break }
    }
    if (-not $found) { throw "mod not built: $m" }
    # Project references the mod was built with - a generated <Game>.Interop.dll,
    # say - must sit next to it: the loader resolves them from the mod's folder.
    # deps.json names them; CoreLoader itself is provided by the loader. They go
    # first, so a running game's hot reload never pairs the new mod with an old
    # dependency.
    $deps = [IO.Path]::ChangeExtension($found, ".deps.json")
    if (Test-Path -LiteralPath $deps) {
        $libs = (Get-Content -LiteralPath $deps -Raw | ConvertFrom-Json).libraries
        foreach ($lib in $libs.PSObject.Properties) {
            if ($lib.Value.type -ne "project") { continue }
            $depName = ($lib.Name -split '/')[0]
            if ($depName -eq "CoreLoader" -or $depName -eq $m) { continue }
            foreach ($ext in ".dll", ".pdb") {
                $depFile = Join-Path (Split-Path -Parent $found) "$depName$ext"
                if (Test-Path -LiteralPath $depFile) { Copy-Item -LiteralPath $depFile -Destination $modsDir -Force }
            }
        }
    }
    Copy-Item -LiteralPath $found -Destination $modsDir -Force
    $pdb = [IO.Path]::ChangeExtension($found, ".pdb")
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $modsDir -Force }
    # A folder named after the mod holds its content (sprites, sounds).
    $content = Join-Path (Split-Path -Parent $found) $m
    if (Test-Path -LiteralPath $content -PathType Container) {
        Copy-Item -LiteralPath $content -Destination $modsDir -Recurse -Force
    }
}

# A test-host marker left by run-game.ps1 -TestHost must not outlive a fresh
# install into normal play; run-game writes it again after deploying.
$marker = Join-Path $loaderDir "testhost.enable"
if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }

Write-Host "Lodestone installed in $GameDir ($($Mods.Count) mod(s))"
if ($Live) { Write-Host "restart the game to run the new loader" }
