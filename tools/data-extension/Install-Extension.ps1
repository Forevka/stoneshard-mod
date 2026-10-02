<#
.SYNOPSIS
    Register the Lodestone/CoreLoader native DLL as a GameMaker extension inside
    a game's data.win, so the game's own extension loader loads it at start-up.

.DESCRIPTION
    This is the "add our DLL as a GameMaker extension" route: no version.dll
    proxy, no process injection, no patched exe. The game loads the DLL through
    the exact mechanism it already uses for its bundled extensions (Steamworks,
    FastAstar, ImGui_GM, ...). On load, the DLL's DllMain boots the loader on its
    own thread, identically to the proxy route (src/dllmain.cpp).

    It drives UndertaleModTool's headless CLI (UndertaleModCli) to edit data.win,
    so the file is re-serialised correctly for the game's GameMaker version rather
    than byte-patched by hand.

    PROOF OF CONCEPT. Test on a disposable copy of the game first. Back up saves.

.PARAMETER GameDir
    The game folder (contains the .exe and data.win).

.PARAMETER Dll
    Path to the built, extension-enabled loader DLL (e.g. build\version.dll after
    rebuilding with src/extension_entry.cpp). Copied into GameDir as -DllName.

.PARAMETER UtmtCli
    Path to UndertaleModCli.exe, or to the folder that contains it.

.PARAMETER ExtensionName
    Name recorded for the extension in data.win. Default: CoreLoaderExt.

.PARAMETER DllName
    Filename the DLL is given inside the game folder and recorded in data.win.
    Default: coreloader_ext.dll.

.PARAMETER FuncName
    Exported symbol GameMaker resolves to trigger the load.
    Default: coreloader_extension_probe.

.PARAMETER DataWin
    Explicit path to data.win. Default: <GameDir>\data.win, else the first *.win
    found under GameDir.

.PARAMETER Revert
    Restore data.win from the backup this script made, and remove the copied DLL.

.EXAMPLE
    .\Install-Extension.ps1 -GameDir "D:\Games\StoneshardCopy\Stoneshard" `
        -Dll ..\..\build\version.dll -UtmtCli "C:\Tools\UndertaleModTool"

.EXAMPLE
    .\Install-Extension.ps1 -GameDir "D:\Games\StoneshardCopy\Stoneshard" -Revert
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameDir,
    [string] $Dll,
    [string] $UtmtCli,
    [string] $ExtensionName = 'CoreLoaderExt',
    [string] $DllName       = 'coreloader_ext.dll',
    [string] $FuncName      = 'coreloader_extension_probe',
    [string] $DataWin,
    [switch] $Revert
)

$ErrorActionPreference = 'Stop'

function Resolve-DataWin {
    param([string] $dir, [string] $explicit)
    if ($explicit) {
        if (-not (Test-Path $explicit)) { throw "data.win not found: $explicit" }
        return (Resolve-Path $explicit).Path
    }
    $candidate = Join-Path $dir 'data.win'
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    $found = Get-ChildItem -Path $dir -Filter *.win -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw "No data.win (or *.win) found under $dir. Pass -DataWin explicitly."
}

if (-not (Test-Path $GameDir)) { throw "GameDir not found: $GameDir" }
$dataWinPath = Resolve-DataWin -dir $GameDir -explicit $DataWin
$backupPath  = "$dataWinPath.lodestone-bak"

# -------------------------------------------------------------------- revert
if ($Revert) {
    if (Test-Path $backupPath) {
        Copy-Item $backupPath $dataWinPath -Force
        Remove-Item $backupPath -Force
        Write-Host "Restored $dataWinPath from backup." -ForegroundColor Green
    } else {
        Write-Warning "No backup at $backupPath; data.win left as-is."
    }
    $stagedDll = Join-Path $GameDir $DllName
    if (Test-Path $stagedDll) {
        Remove-Item $stagedDll -Force
        Write-Host "Removed $stagedDll." -ForegroundColor Green
    }
    Write-Host "Revert complete. (Steam 'Verify integrity' also restores data.win.)"
    return
}

# ------------------------------------------------------------------- install
if (-not $Dll)     { throw "-Dll is required to install (path to the loader DLL)." }
if (-not $UtmtCli) { throw "-UtmtCli is required to install (UndertaleModCli.exe or its folder)." }
if (-not (Test-Path $Dll)) { throw "Loader DLL not found: $Dll" }

# Resolve the CLI: accept the exe directly, or a folder containing it.
$cliExe = $null
if (Test-Path $UtmtCli -PathType Leaf) {
    $cliExe = (Resolve-Path $UtmtCli).Path
} elseif (Test-Path $UtmtCli -PathType Container) {
    $hit = Get-ChildItem -Path $UtmtCli -Recurse -Filter 'UndertaleModCli.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $cliExe = $hit.FullName }
}
if (-not $cliExe) { throw "UndertaleModCli.exe not found at/under: $UtmtCli" }

$script = Join-Path $PSScriptRoot 'add_extension.csx'
if (-not (Test-Path $script)) { throw "Missing $script next to this wrapper." }

# Pristine backup (made once; never overwritten, so re-runs stay reversible).
if (-not (Test-Path $backupPath)) {
    Copy-Item $dataWinPath $backupPath
    Write-Host "Backed up data.win -> $backupPath"
} else {
    Write-Host "Backup already exists: $backupPath (reusing as pristine source)."
}

# Stage the DLL next to the exe under its extension name. GameMaker looks for a
# loose extension file in the game directory.
$stagedDll = Join-Path $GameDir $DllName
Copy-Item $Dll $stagedDll -Force
Write-Host "Staged $Dll -> $stagedDll"

# Parameters reach the .csx through the environment.
$env:CL_EXT_NAME = $ExtensionName
$env:CL_EXT_DLL  = $DllName
$env:CL_EXT_FUNC = $FuncName

# Always build from the pristine backup so repeated runs produce the same result.
Write-Host "Patching data.win via UndertaleModCli ..."
& $cliExe load $backupPath -s $script -o $dataWinPath -f -v
if ($LASTEXITCODE -ne 0) {
    throw "UndertaleModCli exited with code $LASTEXITCODE. data.win may be unchanged; the backup is intact at $backupPath."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Extension : $ExtensionName -> $DllName (fn $FuncName)"
Write-Host "  data.win  : $dataWinPath"
Write-Host "  backup    : $backupPath"
Write-Host ""
Write-Host "Launch the game, then check <GameDir>\Lodestone\Logs\lodestone.log for the"
Write-Host "'=== Lodestone ===' banner. Revert any time with:  -GameDir `"$GameDir`" -Revert"
