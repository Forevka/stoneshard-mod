<#
.SYNOPSIS
  Packs a release: the Lodestone loader zip and one zip per mod.

.DESCRIPTION
  Expects a Release build of both halves (build\version.dll and
  managed\bin\Release). Every zip extracts straight into a game folder:

    Lodestone-<version>-win64.zip   version.dll, Lodestone\ (the managed runtime,
                                    the analyzer for mod authors, and with
                                    -BundleRuntime a private .NET runtime in
                                    Lodestone\dotnet\), and Lodestone\README.txt
    <Mod>-<version>.zip             Mods\<Mod>.dll, the project dlls it was built
                                    with (a generated interop), and Mods\<Mod>\
                                    content

  A mod written against a generated interop (<InteropGame> in its csproj) only
  compiles where that game's interop exists. Built without it, the dll is empty,
  so such a mod is skipped with a warning rather than shipped hollow. Build and
  upload those on a machine with the game: -Mods FastTravel,Reliquary -NoLoader
  -Upload v<version>.

.EXAMPLE
  tools\package-release.ps1 -BundleRuntime
.EXAMPLE
  dotnet build managed\CoreLoader.sln -c Release
  tools\package-release.ps1 -Mods FastTravel,Reliquary -NoLoader -Upload v0.4.0
#>
#Requires -Version 7
# Windows PowerShell's Compress-Archive writes entry names with backslashes,
# which some unzip tools read as one flat file name.
param(
    [string]   $OutDir = "",
    [string[]] $Mods = @(),                      # default: every mod under managed\Mods but the excluded
    [string[]] $Exclude = @("ContentDemo"),      # demos that are not worth a player's download
    [switch]   $NoLoader,
    [switch]   $BundleRuntime,
    [string]   $RuntimeVersion = "",             # default: the newest .NET 10 runtime
    [string]   $Upload = ""                      # a release tag: gh release upload, replacing same-named assets
)

$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $root "managed\bin\Release"
$version = ([xml](Get-Content -LiteralPath (Join-Path $root "managed\CoreLoader\CoreLoader.csproj") -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "no <Version> in managed\CoreLoader\CoreLoader.csproj" }
if (-not $OutDir) { $OutDir = Join-Path $root "dist" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) "lodestone-package-$PID"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$made = @()

function New-Zip([string] $From, [string] $Name) {
    $zip = Join-Path $OutDir $Name
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $From "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "packed $Name ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB)"
    return $zip
}

try {
    # ------------------------------------------------------------------ loader
    if (-not $NoLoader) {
        # Ninja builds land in build\, Visual Studio ones in build\Release\; with
        # both on a machine, the newer one is the build that was meant.
        $native = @("build\version.dll", "build\Release\version.dll") | ForEach-Object { Join-Path $root $_ } |
            Where-Object { Test-Path -LiteralPath $_ } | Get-Item | Sort-Object LastWriteTime -Descending |
            Select-Object -First 1 -ExpandProperty FullName
        if (-not $native) { throw "version.dll not found under build\ - build the native loader first" }
        if (-not (Test-Path -LiteralPath (Join-Path $managed "CoreLoader\CoreLoader.dll"))) {
            throw "CoreLoader.dll not found - run: dotnet build managed\CoreLoader.sln -c Release"
        }

        $dir = Join-Path $stage "loader"
        $lode = Join-Path $dir "Lodestone"
        New-Item -ItemType Directory -Force -Path $lode | Out-Null
        Copy-Item -LiteralPath $native -Destination $dir
        # The same files deploy-coreloader.ps1 installs, the analyzer folder included:
        # the dotnet-new template compiles mods against this folder.
        Copy-Item -Path (Join-Path $managed "CoreLoader\*") -Destination $lode -Recurse

        if ($BundleRuntime) {
            # A private runtime in Lodestone\dotnet wins over any installed one, so
            # players never have to install .NET themselves.
            $meta = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json"
            if (-not $RuntimeVersion) { $RuntimeVersion = $meta.'latest-runtime' }
            $file = $meta.releases | Where-Object { $_.runtime.version -eq $RuntimeVersion } | Select-Object -First 1 |
                ForEach-Object { $_.runtime.files } | Where-Object { $_.name -eq "dotnet-runtime-win-x64.zip" } |
                Select-Object -First 1
            if (-not $file) { throw ".NET runtime $RuntimeVersion (win-x64 zip) is not in the 10.0 release metadata" }
            $rtZip = Join-Path $stage "dotnet-runtime.zip"
            Write-Host "downloading .NET runtime $RuntimeVersion"
            $ProgressPreference = "SilentlyContinue"   # the progress bar slows a large download to a crawl
            Invoke-WebRequest -Uri $file.url -OutFile $rtZip
            if ((Get-FileHash -LiteralPath $rtZip -Algorithm SHA512).Hash -ne $file.hash) {
                throw "the downloaded .NET runtime does not match its published SHA-512"
            }
            Expand-Archive -LiteralPath $rtZip -DestinationPath (Join-Path $lode "dotnet")
        }

        $runtimeLine = "Needs the .NET 10 runtime (x64): https://dotnet.microsoft.com/download/dotnet/10.0"
        if ($BundleRuntime) { $runtimeLine = "A private .NET $RuntimeVersion runtime is included; nothing else to install." }
        @"
Lodestone $version - a mod loader for GameMaker (YYC) games

INSTALL
  Extract everything into the game's folder, next to the game's .exe, so that
  version.dll sits beside it. Mods go in the Mods folder, which is created on the
  first launch; a mod's zip also extracts straight into the game folder.
  $runtimeLine

IN GAME
  INSERT opens the Lodestone overlay.

  If the game folder already has a version.dll (another mod loader, say), back it
  up first: this one replaces it.

UNINSTALL
  Delete version.dll, the Lodestone folder and the Mods folder, and put back any
  version.dll you backed up.

TROUBLESHOOTING
  The log is Lodestone\Logs\lodestone.log (the previous run: lodestone.prev.log).
  Attach it to a bug report.
"@ | Set-Content -LiteralPath (Join-Path $lode "README.txt") -Encoding utf8

        $made += New-Zip $dir "Lodestone-$version-win64.zip"
    }

    # -------------------------------------------------------------------- mods
    if (-not $Mods) {
        $Mods = Get-ChildItem -LiteralPath (Join-Path $root "managed\Mods") -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj") } |
            ForEach-Object Name | Where-Object { $_ -notin $Exclude }
    }
    # `powershell -File ... -Mods A,B` hands over "A,B" as ONE string; split it.
    $Mods = @($Mods | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

    foreach ($m in $Mods) {
        $dll = Join-Path $managed "Mods\$m.dll"
        if (-not (Test-Path -LiteralPath $dll)) { throw "mod not built: $m" }

        # Project references the mod was built with, from its deps.json - the same
        # rule deploy-coreloader.ps1 follows. CoreLoader comes with the loader.
        $deps = @()
        $depsJson = [IO.Path]::ChangeExtension($dll, ".deps.json")
        if (Test-Path -LiteralPath $depsJson) {
            foreach ($lib in (Get-Content -LiteralPath $depsJson -Raw | ConvertFrom-Json).libraries.PSObject.Properties) {
                if ($lib.Value.type -ne "project") { continue }
                $name = ($lib.Name -split '/')[0]
                if ($name -ne "CoreLoader" -and $name -ne $m) { $deps += $name }
            }
        }
        $csproj = Get-ChildItem -LiteralPath (Join-Path $root "managed\Mods\$m") -Filter "*.csproj" | Select-Object -First 1
        if ($csproj -and (Select-String -LiteralPath $csproj.FullName -Pattern '<InteropGame>' -Quiet) -and
            -not ($deps | Where-Object { $_ -like "*.Interop" })) {
            Write-Warning "$m needs a generated interop that this build did not have; skipped (package it where the game is installed)"
            continue
        }

        $dir = Join-Path $stage "mod-$m"
        $modsDir = Join-Path $dir "Mods"
        New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
        Copy-Item -LiteralPath $dll -Destination $modsDir
        $pdb = [IO.Path]::ChangeExtension($dll, ".pdb")
        if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $modsDir }   # line numbers in fault logs
        foreach ($d in $deps) {
            $depDll = Join-Path $managed "Mods\$d.dll"
            if (-not (Test-Path -LiteralPath $depDll)) { throw "$m depends on $d, which was not built next to it" }
            Copy-Item -LiteralPath $depDll -Destination $modsDir
        }
        $content = Join-Path $managed "Mods\$m"
        if (Test-Path -LiteralPath $content -PathType Container) { Copy-Item -LiteralPath $content -Destination $modsDir -Recurse }

        $made += New-Zip $dir "$m-$version.zip"
    }

    if ($Upload) {
        if (-not $made) { throw "nothing was packed, so nothing to upload" }
        & gh release upload $Upload @made --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
        Write-Host "uploaded $($made.Count) file(s) to release $Upload"
    }
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
