<#
.SYNOPSIS
  Drives a running game through CoreLoader's test host.

.DESCRIPTION
  The game must have been started with CORELOADER_TEST=1 (tools\run-game.ps1
  -TestHost does that). The loader then serves a named pipe, current user
  only, and writes its name to <game>\CoreLoader\Logs\testhost.pipe.

  As a command:   tools\coreloader.ps1 [-Game Stoneshard|Dwarf] <cmd> [args...]
  prints the result as JSON and exits 0, or prints the error and exits 1
  (2 when the game cannot be reached, 3 when it did not answer in time; such a
  request is dropped by the game unrun). Arguments that read as numbers, true,
  false or null are sent as such; wrap one in double quotes ('"123"') to send
  it as a string.

  Dot-sourced:    . tools\coreloader.ps1 -Game Stoneshard
  defines Invoke-CoreLoader <cmd> [args...] (returns the result, throws on
  ok:false) and Wait-CoreLoader { condition } (polls until the block returns
  something truthy). Arguments passed from PowerShell keep their types.

  'list-commands' lists every command, including the ones mods register.

.EXAMPLE
  tools\coreloader.ps1 -Game Stoneshard call scr_atr STR
  tools\coreloader.ps1 -Game Dwarf builtin string_upper abc
  tools\coreloader.ps1 -Game Stoneshard cheats.atr-set STR 20
  . tools\coreloader.ps1 -Game Stoneshard; Invoke-CoreLoader status
#>
param(
    [ValidateSet("Stoneshard", "Dwarf")] [string] $Game,
    [string] $GameDir,
    # The pipe's name (coreloader-<pid>), instead of reading it from the game folder.
    [string] $Pipe,
    [int]    $TimeoutSec = 30,
    # A self for call/builtin: "current" is the instance the game last ran, a
    # number is an instance id.
    [string] $As,
    [Parameter(Position = 0)] [string] $Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)] [object[]] $Arguments = @()
)

$Known = @{
    Stoneshard = "D:\torrent\Stoneshard (Early Access)\Stoneshard"
    Dwarf      = "D:\SteamLibrary\steamapps\common\Dwarf Eats Mountain Demo"
}

function Find-CoreLoaderPipe {
    param([string] $Game, [string] $GameDir)
    if ($Game -and -not $GameDir) { $GameDir = $Known[$Game] }
    $dirs = if ($GameDir) { @($GameDir) } else { @($Known.Values) }
    # The newest name file wins when no game was named; a stale one (the game
    # crashed) simply fails to connect.
    $file = $dirs | ForEach-Object { Join-Path $_ "CoreLoader\Logs\testhost.pipe" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending |
        Select-Object -First 1
    if (-not $file) { throw "no testhost.pipe found - start the game with tools\run-game.ps1 -TestHost (CORELOADER_TEST=1)" }
    return (Get-Content -LiteralPath $file -Raw).Trim()
}

$script:CoreLoaderPipe = $Pipe
$script:CoreLoaderPipeGiven = [bool]$Pipe
$script:CoreLoaderGame = $Game
$script:CoreLoaderGameDir = $GameDir
$script:CoreLoaderTimeoutSec = $TimeoutSec
$script:CoreLoaderNextId = 0

function Invoke-CoreLoader {
    <#
    .SYNOPSIS
      Sends one command; returns its result, or throws with the game's error.
      -Raw returns the whole response ({id, ok, result, error}) and never throws on ok:false.
    #>
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Command,
        [Parameter(Position = 1, ValueFromRemainingArguments = $true)] [object[]] $Arguments = @(),
        [string] $As,
        [int]    $TimeoutSec = $script:CoreLoaderTimeoutSec,
        [switch] $Raw
    )
    $script:CoreLoaderNextId++
    # The server drops the request if its game thread has not reached it by
    # this timeout, so a command reported here as timed out never runs later.
    $request = [ordered]@{ id = $script:CoreLoaderNextId; cmd = $Command; args = @($Arguments); timeout = $TimeoutSec }
    if ($As) { $request.as = $As }
    $json = ConvertTo-Json -InputObject $request -Compress -Depth 10

    # A pipe named by -Pipe is used as given. A discovered one is looked up
    # again when it stops answering: a restarted game has a new pid.
    $client = $null
    foreach ($attempt in 1, 2) {
        $fixed = [bool]$script:CoreLoaderPipe -and $script:CoreLoaderPipeGiven
        if (-not $script:CoreLoaderPipe) {
            $script:CoreLoaderPipe = Find-CoreLoaderPipe -Game $script:CoreLoaderGame -GameDir $script:CoreLoaderGameDir
        }
        # Asynchronous, so a read that times out can be cancelled and the pipe
        # closed at once. CurrentUserOnly: the server end must be this user's.
        $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $script:CoreLoaderPipe,
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly)
        try { $client.Connect([Math]::Min($TimeoutSec, 10) * 1000); break }
        catch {
            $client.Dispose(); $client = $null
            $why = $_.Exception.Message
            if ($fixed -or $attempt -eq 2) {
                throw [System.IO.IOException]::new("cannot connect to \\.\pipe\$($script:CoreLoaderPipe): is the game running with CORELOADER_TEST=1? ($why)")
            }
            $script:CoreLoaderPipe = $null
        }
    }

    $cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSec))
    try {
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $writer = [System.IO.StreamWriter]::new($client, $utf8)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $utf8)
        $writer.WriteLine($json)
        try { $line = $reader.ReadLineAsync($cts.Token).AsTask().GetAwaiter().GetResult() }
        catch [System.OperationCanceledException] {
            throw [System.TimeoutException]::new("no answer to '$Command' within $TimeoutSec s")
        }
        if ($null -eq $line) { throw [System.IO.IOException]::new("the game closed the pipe without answering '$Command'") }
    }
    finally {
        $cts.Dispose()
        $client.Dispose()
    }

    $response = $line | ConvertFrom-Json
    if ($Raw) { return $response }
    if (-not $response.ok) { throw "$Command failed: $($response.error)" }
    return $response.result
}

function Wait-CoreLoader {
    <#
    .SYNOPSIS
      Polls until the script block returns something truthy, and returns it.
      Errors inside the block count as "not yet". Throws on timeout.
    .EXAMPLE
      Wait-CoreLoader { $r = Invoke-CoreLoader cheats.potion-result; if (-not $r.pending) { $r } } -TimeoutSec 10
    #>
    param(
        [Parameter(Mandatory, Position = 0)] [scriptblock] $Until,
        [int] $TimeoutSec = 30,
        [int] $IntervalMs = 250,
        [string] $What = "condition"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null
    do {
        try {
            $r = & $Until
            if ($r) { return $r }
        }
        catch { $last = $_.Exception.Message }
        Start-Sleep -Milliseconds $IntervalMs
    } while ((Get-Date) -lt $deadline)
    throw "timed out after $TimeoutSec s waiting for $What$(if ($last) { " (last error: $last)" })"
}

# Command-line words to JSON values: numbers, booleans and null by their
# spelling; a word in double quotes is always a string.
function ConvertFrom-CliArgument([object] $a) {
    if ($a -isnot [string]) { return $a }
    if ($a.Length -ge 2 -and $a.StartsWith('"') -and $a.EndsWith('"')) { return $a.Substring(1, $a.Length - 2) }
    if ($a -eq "true") { return $true }
    if ($a -eq "false") { return $false }
    if ($a -eq "null") { return $null }
    $d = 0.0
    if ([double]::TryParse($a, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $d)) { return $d }
    return $a
}

# Dot-sourced without a command: only the functions.
if (-not $Command) { return }

try {
    $values = @($Arguments | ForEach-Object { ConvertFrom-CliArgument $_ })
    $response = Invoke-CoreLoader $Command @values -As $As -Raw
}
catch {
    $ex = $_.Exception
    while ($ex.InnerException -and $ex -is [System.Management.Automation.MethodInvocationException]) { $ex = $ex.InnerException }
    [Console]::Error.WriteLine($ex.Message)
    # 3: sent, but no answer in time (the server drops it unrun); 2: unreachable.
    exit $(if ($ex -is [System.TimeoutException]) { 3 } else { 2 })
}
if ($response.ok) {
    $r = $response.result
    if ($null -eq $r -or $r -is [string] -or $r -is [ValueType]) { Write-Output $r }
    else { Write-Output (ConvertTo-Json -InputObject $r -Depth 10) }
    exit 0
}
[Console]::Error.WriteLine("$Command failed: $($response.error)")
exit 1
