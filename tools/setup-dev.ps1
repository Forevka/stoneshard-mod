<#
.SYNOPSIS
  Points the managed solution at the games Lodestone is installed in, so
  projects written against a game's generated interop build - and navigate -
  in Visual Studio.

.DESCRIPTION
  Finds game folders with Lodestone installed (given with -GameDir, found in
  your Steam libraries, and any already listed before), then writes:

    managed\CoreLoader.user.props   CoreLoaderGameDirs for every build (VS and dotnet)
    managed\CoreLoader.Dev.sln      CoreLoader.sln plus each game's generated
                                    <Game>.Interop project, so Go To Definition
                                    lands in the generated source

  Both are per-machine and ignored by git. Open CoreLoader.Dev.sln to work on
  interop-based mods; CoreLoader.sln keeps working without any of this.
  A game's interop exists once the game has been run with Lodestone installed.
  Re-run after installing Lodestone into another game.

.EXAMPLE
  tools\setup-dev.ps1
  tools\setup-dev.ps1 -GameDir "D:\Games\Stoneshard"
#>
param(
    [string[]] $GameDir = @(),
    [switch]   $NoSolution
)

$ErrorActionPreference = "Stop"
$root     = Split-Path -Parent $PSScriptRoot
$managed  = Join-Path $root "managed"
$propsOut = Join-Path $managed "CoreLoader.user.props"
$slnIn    = Join-Path $managed "CoreLoader.sln"
$slnOut   = Join-Path $managed "CoreLoader.Dev.sln"

$dirs = [System.Collections.Generic.List[string]]::new()
function Add-GameDir([string] $d, [bool] $explicit) {
    if (-not $d) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $d "Lodestone"))) {
        if ($explicit) { Write-Warning "no Lodestone installed in $d (tools\deploy-coreloader.ps1 -GameDir ...)" }
        return
    }
    $full = (Resolve-Path -LiteralPath $d).Path.TrimEnd('\')
    if (-not ($dirs | Where-Object { $_ -ieq $full })) { $dirs.Add($full) }
}

# 1. Folders named on the command line (';' or ',' separated also accepted).
foreach ($d in ($GameDir | ForEach-Object { $_ -split '[;,]' })) { Add-GameDir $d.Trim() $true }

# 2. Folders already listed from an earlier run.
if (Test-Path -LiteralPath $propsOut) {
    $m = [regex]::Match((Get-Content -LiteralPath $propsOut -Raw), '<CoreLoaderGameDirs>(.*?)</CoreLoaderGameDirs>')
    if ($m.Success) { foreach ($d in $m.Groups[1].Value -split ';') { Add-GameDir $d.Trim() $false } }
}

# 3. Every Steam library: games with a Lodestone folder.
$steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if ($steam) {
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    $libraries = @($steam)
    if (Test-Path -LiteralPath $vdf) {
        $libraries += [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in $libraries | Select-Object -Unique) {
        $common = Join-Path $lib "steamapps\common"
        if (-not (Test-Path -LiteralPath $common)) { continue }
        Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "Lodestone") } |
            ForEach-Object { Add-GameDir $_.FullName $false }
    }
}

if ($dirs.Count -eq 0) {
    Write-Warning "no game with Lodestone installed was found; pass -GameDir <game folder>"
    return
}

# The settings every build reads (managed\Directory.Build.props imports it).
$escaped = ($dirs | ForEach-Object { [System.Security.SecurityElement]::Escape($_) }) -join ';'
@"
<Project>
  <!-- Written by tools\setup-dev.ps1 - per machine, not in git. Games with
       Lodestone installed; their generated interop is found under each. -->
  <PropertyGroup>
    <CoreLoaderGameDirs>$escaped</CoreLoaderGameDirs>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $propsOut -Encoding UTF8
Write-Host "wrote $propsOut"

# The generated interop projects that exist right now.
$interop = foreach ($d in $dirs) {
    $base = Join-Path $d "Lodestone\Interop"
    if (Test-Path -LiteralPath $base) {
        Get-ChildItem -LiteralPath $base -Directory |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter "*.Interop.csproj" -File }
    }
}
foreach ($d in $dirs) {
    $n = @($interop | Where-Object { $_.FullName.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) }).Count
    Write-Host ("  {0}  ({1})" -f $d, $(if ($n) { "$n interop project(s)" } else { "no interop yet - run the game once" }))
}

if ($NoSolution) { return }

# A copy of the solution with the interop projects in it, so their generated
# source is part of what Visual Studio loads and navigates.
Copy-Item -LiteralPath $slnIn -Destination $slnOut -Force
foreach ($p in $interop) {
    & dotnet sln $slnOut add $p.FullName --solution-folder "Interop (generated)" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "could not add $($p.FullName) to $slnOut" }
}
Write-Host "wrote $slnOut - open it in Visual Studio to build and navigate against the interop"
