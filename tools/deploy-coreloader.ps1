<#
.SYNOPSIS
  Installs CoreLoader (and optionally mods) into a YYC GameMaker game folder.

.DESCRIPTION
  Copies build\version.dll next to the game exe, the managed runtime into
  <game>\CoreLoader\, and the named mods from managed\bin\<Config>\Mods (or
  TestMods) into <game>\Mods\. An existing version.dll that is not ours is
  kept as version.dll.bak the first time. Works while the game is closed; a
  running game locks version.dll.

.EXAMPLE
  tools\deploy-coreloader.ps1 -GameDir "D:\Games\Stoneshard" -Mods ScriptSpy,GlobalsEditor
#>
param(
    [Parameter(Mandatory)] [string]   $GameDir,
    [string[]] $Mods = @(),
    [string]   $Configuration = "Release",
    [switch]   $CleanMods
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

$target = Join-Path $GameDir "version.dll"
$backup = Join-Path $GameDir "version.dll.bak"
# A CoreLoader folder means the version.dll there is already ours; only a
# foreign one (another mod loader, an older build of this mod) is worth keeping.
$ours = Test-Path -LiteralPath (Join-Path $GameDir "CoreLoader\CoreLoader.dll")
if ((Test-Path -LiteralPath $target) -and -not $ours -and -not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup
    Write-Host "kept the previous version.dll as version.dll.bak"
}
Copy-Item -LiteralPath $native -Destination $target -Force

$loaderDir = Join-Path $GameDir "CoreLoader"
New-Item -ItemType Directory -Force -Path $loaderDir | Out-Null
Get-ChildItem -LiteralPath (Join-Path $managed "CoreLoader") -File |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $loaderDir -Force }

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
    Copy-Item -LiteralPath $found -Destination $modsDir -Force
    $pdb = [IO.Path]::ChangeExtension($found, ".pdb")
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $modsDir -Force }
    # A folder named after the mod holds its content (sprites, sounds).
    $content = Join-Path (Split-Path -Parent $found) $m
    if (Test-Path -LiteralPath $content -PathType Container) {
        Copy-Item -LiteralPath $content -Destination $modsDir -Recurse -Force
    }
}

Write-Host "CoreLoader installed in $GameDir ($($Mods.Count) mod(s))"
