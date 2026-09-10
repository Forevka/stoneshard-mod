# Deploys version.dll into the game folder even while the game is RUNNING.
#
# Windows will not let a loaded DLL be overwritten, but it will let it be
# RENAMED - the lock is on the path, not the bytes. So the live file is moved
# aside and the new build takes its name. The running process keeps executing
# the renamed image and is completely unaffected; the next launch picks up the
# new one.
#
# This removes the "close the game before deploying" step. It does NOT make the
# mod hot-reload: a restart is still needed to RUN the new code.

param(
    [string]$Source = "$PSScriptRoot\..\build\version.dll",
    [string]$GameDir = $(if ($env:STONESHARD_DIR) { $env:STONESHARD_DIR }
                        else { "C:\Program Files (x86)\Steam\steamapps\common\Stoneshard" })
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $GameDir 'version.dll'

if (-not (Test-Path $Source)) { throw "no build at $Source - build first" }

# Clear out the previous stand-aside copy. It is only deletable once the process
# that had it loaded has exited, so a failure here is expected and harmless.
$old = Join-Path $GameDir 'version.dll.old'
if (Test-Path $old) {
    try { Remove-Item $old -Force } catch { Write-Host "note: $old still in use, leaving it" }
}

if (Test-Path $target) {
    try {
        Copy-Item $Source $target -Force        # fast path: nothing has it open
        Write-Host "deployed (overwrote in place)"
        exit 0
    } catch {
        Move-Item $target $old -Force           # locked: move the live one aside
        Write-Host "game is running - moved the live dll to version.dll.old"
    }
}

Copy-Item $Source $target -Force
$info = Get-Item $target
Write-Host ("deployed {0:N0} bytes -> {1}" -f $info.Length, $target)
Write-Host "restart Stoneshard to run it"
