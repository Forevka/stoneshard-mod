# Two-instance test harness for co-op bring-up (plan M0).
#
# Each game process gets its own SSMOD_DATA_DIR, so the two never share a log,
# an imgui.ini, or - the one that actually breaks things - debug-cmd.txt, which
# remote::Poll consumes by deleting.
#
#   . .\tools\coop.ps1
#   Start-SsInstance host
#   Start-SsInstance client
#   Send-SsCmd host 'status'
#   Get-SsReply host

$script:SsRoot = Split-Path -Parent $PSScriptRoot
$script:SsExe  = "D:\torrent\Stoneshard (Early Access)\Stoneshard\StoneShard.exe"

function Get-SsDataDir([string]$Role) {
    Join-Path $script:SsRoot "run\$Role"
}

# Launches one game with its own mod data dir. Returns the process object.
function Start-SsInstance {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'client')][string]$Role,
        [string]$Exe = $script:SsExe
    )

    $dir = Get-SsDataDir $Role
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # Clear the previous run's artifacts so a stale reply is never mistaken for
    # a fresh one.
    Get-ChildItem -Path $dir -Filter 'debug-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName         = $Exe
    $psi.WorkingDirectory = Split-Path -Parent $Exe
    $psi.UseShellExecute  = $false
    $psi.EnvironmentVariables['SSMOD_DATA_DIR'] = $dir

    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Host "started '$Role' pid=$($proc.Id) datadir=$dir"
    $proc
}

# Writes one command line for that instance to pick up. It polls once a second.
function Send-SsCmd {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'client')][string]$Role,
        [Parameter(Mandatory)][string]$Command
    )
    # No trailing newline: remote::Poll reads with fgets, which keeps it, and
    # console::Tokenize strips only spaces and tabs - so "call foo\n" looks up
    # the symbol "foo\n" and fails silently into the ImGui buffer.
    [System.IO.File]::WriteAllText((Join-Path (Get-SsDataDir $Role) 'debug-cmd.txt'), $Command)
}

function Get-SsReply {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'client')][string]$Role,
        [int]$Tail = 20
    )
    $f = Join-Path (Get-SsDataDir $Role) 'debug-reply.txt'
    if (Test-Path $f) { Get-Content $f -Tail $Tail } else { "(no reply file yet at $f)" }
}

function Get-SsLog {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'client')][string]$Role,
        [int]$Tail = 40
    )
    $f = Join-Path (Get-SsDataDir $Role) 'stoneshard-mod.log'
    if (Test-Path $f) { Get-Content $f -Tail $Tail } else { "(no log yet at $f)" }
}

# Send a command and wait for the reply file to grow. The mod polls at 1 Hz, so
# anything under ~2s of patience will look like a failure when it is not.
function Invoke-SsCmd {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'client')][string]$Role,
        [Parameter(Mandatory)][string]$Command,
        [int]$TimeoutSec = 8
    )
    $f      = Join-Path (Get-SsDataDir $Role) 'debug-reply.txt'
    $before = if (Test-Path $f) { (Get-Item $f).Length } else { 0 }

    Send-SsCmd $Role $Command

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ((Test-Path $f) -and (Get-Item $f).Length -gt $before) {
            Start-Sleep -Milliseconds 400        # let multi-line replies finish
            return Get-Content $f -Tail 30
        }
    }
    "(timed out after ${TimeoutSec}s - is the game running and past its first frames?)"
}

# The client's save file must be byte-identical across a session (plan §2).
function Get-SsSaveHash {
    $dir = Join-Path $env:LOCALAPPDATA 'StoneShard\characters_v1'
    if (-not (Test-Path $dir)) { return "(no save dir at $dir)" }
    Get-ChildItem $dir -Recurse -File |
        Sort-Object FullName |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($dir.Length + 1) }
}
