<#
.SYNOPSIS
  Opens or runs the RANGBAAZI Godot project with the right Godot build (4.7.2 .NET).

.DESCRIPTION
  editor  Open the Godot editor on game/ (default).
  run     Build the C# code, then run the game with logs in this terminal.
  smoke   Build, then run the headless smoke test: two sparring bots play 900 frames
          through the real key bindings. Exit code 0 = pass, 1 = fail.
  record  Build, then record the smoke match to -Out (.avi, or .png for a frame sequence),
          relative to the current directory. Windowed: don't touch keyboard or pads meanwhile.

  Online 1v1 (rollback netcode over UDP; see README "Online 1v1"):
  online        Build, then play online against another PC: -Peer <their IPv4>:<their port>,
                -Side 1 or 2 (the other player picks the other side), -Port <yours> (7000).
                Optional: -Delay <input delay frames> (2), -Bot easy|normal|hard (a bot plays you).
  online-local  Build, then open two windows on this PC talking over 127.0.0.1: you are P1
                (port 7000), a bot plays P2 (port 7001). -Bot sets its level (normal).
  online-smoke  Build, then run two headless games with bots that play -Frames frames (900)
                online over 127.0.0.1. Exit code 0 = both passed and agree on the checksum.

  Don't use the "godot" alias that winget adds: it is a symlink, so Godot looks for its
  .NET assemblies next to the link, fails with ".NET: Assemblies not found" and crashes.
  This script finds the real executable. To override, set $env:GODOT to Godot's
  *_console.exe (a symlink or the GUI exe next to it also works).

.EXAMPLE
  ./tools/godot.ps1
  ./tools/godot.ps1 smoke
  ./tools/godot.ps1 record -Out "$env:TEMP\smoke.avi"
  ./tools/godot.ps1 online -Peer 192.168.1.20:7000 -Side 1
  ./tools/godot.ps1 online-local
  ./tools/godot.ps1 online-smoke
#>
param(
    [ValidateSet('editor', 'run', 'smoke', 'record', 'online', 'online-local', 'online-smoke')]
    [string]$Mode = 'editor',
    [string]$Out = '',
    [int]$Port = 7000,
    [string]$Peer = '',
    [ValidateSet(0, 1, 2)]
    [int]$Side = 0,
    [ValidateRange(0, 7)]
    [int]$Delay = 2,
    [ValidateSet('', 'none', 'easy', 'normal', 'hard')]
    [string]$Bot = '',
    [int]$Frames = 900
)

$ErrorActionPreference = 'Stop'
$GodotVersion = '4.7.2'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\game')).Path

function Resolve-GodotOverride([string]$path) {
    if (-not (Test-Path $path)) { throw "GODOT is set to '$path', which does not exist." }
    $item = Get-Item $path
    # Follow symlinks (like the winget alias) so Godot finds GodotSharp/ next to the real exe.
    while ($item.LinkType -eq 'SymbolicLink') {
        $target = @($item.Target)[0]
        if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $item.DirectoryName $target }
        $item = Get-Item $target
    }
    $exe = $item.FullName
    # Only the *_console.exe is a console program. PowerShell doesn't wait for the GUI exe or
    # report its exit code, so smoke would "pass" without running.
    if ($exe -notmatch '_console\.exe$') {
        $console = $exe -replace '\.exe$', '_console.exe'
        if (-not (Test-Path $console)) { throw "GODOT must point at Godot's *_console.exe (got '$exe')." }
        $exe = $console
    }
    return $exe
}

function Find-GodotConsole {
    if ($env:GODOT) { return Resolve-GodotOverride $env:GODOT }
    $name = "Godot_v$GodotVersion-stable_mono_win64_console.exe"
    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'),
        (Join-Path $env:ProgramFiles 'WinGet\Packages')
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $exe = Get-ChildItem $root -Directory -Filter 'GodotEngine.GodotEngine.Mono*' |
            ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter $name -ErrorAction SilentlyContinue } |
            Select-Object -First 1
        if ($exe) { return $exe.FullName }
    }
    throw "Godot $GodotVersion .NET not found. Install it with: winget install --id GodotEngine.GodotEngine.Mono --exact --version $GodotVersion (or set GODOT to its *_console.exe)."
}

function Build-Game {
    # Running a project without the editor does not compile C#, so build first.
    & dotnet build (Join-Path $project 'Rangbaazi.csproj') -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "C# build failed." }
}

function Get-OnlineArgs([int]$port, [string]$peer, [int]$side, [string]$bot) {
    $a = @('--online', '--port', "$port", '--peer', $peer, '--side', "$side", '--delay', "$Delay")
    if ($bot -and $bot -ne 'none') { $a += @('--bot', $bot) }
    return $a
}

$console = Find-GodotConsole

switch ($Mode) {
    'editor' {
        # The non-console exe sits next to the console one; it doesn't hold this terminal.
        $gui = $console -replace '_console\.exe$', '.exe'
        Start-Process -FilePath $gui -ArgumentList @('--editor', '--path', "`"$project`"")
    }
    'run' {
        Build-Game
        $godotArgs = @('--path', $project)
        & $console @godotArgs
        exit $LASTEXITCODE
    }
    'smoke' {
        Build-Game
        # Array splatting keeps the literal "--" that separates Godot's args from the game's.
        $godotArgs = @('--headless', '--path', $project, '--fixed-fps', '60', '--', '--smoke')
        & $console @godotArgs
        exit $LASTEXITCODE
    }
    'record' {
        if (-not $Out) { throw "record needs -Out <file.avi|file.png>." }
        # Godot resolves a relative movie path against the project (res://), so make it absolute
        # against the caller's directory, and make sure its folder exists.
        $moviePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
        $movieDir = Split-Path -Parent $moviePath
        if ($movieDir -and -not (Test-Path $movieDir)) { New-Item -ItemType Directory -Force -Path $movieDir | Out-Null }
        Build-Game
        $godotArgs = @('--path', $project, '--write-movie', $moviePath, '--', '--smoke')
        & $console @godotArgs
        exit $LASTEXITCODE
    }
    'online' {
        if (-not $Peer -or $Side -eq 0) { throw "online needs -Peer <their IPv4>:<their port> and -Side 1 or 2." }
        Build-Game
        $godotArgs = @('--path', $project, '--') + (Get-OnlineArgs $Port $Peer $Side $Bot)
        & $console @godotArgs
        exit $LASTEXITCODE
    }
    'online-local' {
        Build-Game
        $botLevel = if ($Bot) { $Bot } else { 'normal' }
        # P2 in its own window (no console), P1 here with logs in this terminal.
        $gui = $console -replace '_console\.exe$', '.exe'
        $p2Args = @('--path', "`"$project`"", '--') + (Get-OnlineArgs 7001 '127.0.0.1:7000' 2 $botLevel)
        $p2 = Start-Process -FilePath $gui -ArgumentList $p2Args -PassThru
        try {
            $godotArgs = @('--path', $project, '--') + (Get-OnlineArgs 7000 '127.0.0.1:7001' 1 '')
            & $console @godotArgs
            exit $LASTEXITCODE
        }
        finally {
            if (-not $p2.HasExited) { Stop-Process -Id $p2.Id -ErrorAction SilentlyContinue }
        }
    }
    'online-smoke' {
        Build-Game
        $logDir = Join-Path ([IO.Path]::GetTempPath()) "rangbaazi-online-smoke-$PID"
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $sides = @(
            @{ Side = 1; Port = 7100; Peer = '127.0.0.1:7101'; Bot = 'normal' },
            @{ Side = 2; Port = 7101; Peer = '127.0.0.1:7100'; Bot = 'hard' }
        )
        $procs = foreach ($s in $sides) {
            $a = @('--headless', '--path', "`"$project`"", '--') + (Get-OnlineArgs $s.Port $s.Peer $s.Side $s.Bot) + @('--frames', "$Frames")
            $p = Start-Process -FilePath $console -ArgumentList $a -PassThru -NoNewWindow `
                -RedirectStandardOutput (Join-Path $logDir "p$($s.Side).out.txt") `
                -RedirectStandardError (Join-Path $logDir "p$($s.Side).err.txt")
            $null = $p.Handle # without this, PowerShell 5.1 never reports the exit code
            $p
        }
        $deadline = (Get-Date).AddSeconds(180)
        foreach ($p in $procs) {
            $left = [Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds)
            if (-not $p.WaitForExit([int]$left)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }
        $ok = $true
        $checksums = @()
        foreach ($s in $sides) {
            $p = $procs[$s.Side - 1]
            $p.WaitForExit()
            # Not $out: PowerShell names ignore case, and $Out is this script's [string] parameter.
            $stdout = Get-Content (Join-Path $logDir "p$($s.Side).out.txt") -ErrorAction SilentlyContinue
            $stderr = Get-Content (Join-Path $logDir "p$($s.Side).err.txt") -ErrorAction SilentlyContinue
            $pass = $stdout | Where-Object { $_ -like 'ONLINE SMOKE PASS*' } | Select-Object -Last 1
            if ($p.ExitCode -ne 0 -or -not $pass) {
                $ok = $false
                Write-Host "P$($s.Side) FAILED (exit code $($p.ExitCode)). Last output:"
                @(@($stdout) + @($stderr) | Where-Object { $_ }) | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" }
            }
            else {
                Write-Host $pass
                if ($pass -match 'checksum=([0-9A-F]{16})') { $checksums += $Matches[1] }
            }
        }
        if ($ok -and ($checksums.Count -ne 2 -or $checksums[0] -ne $checksums[1])) {
            $ok = $false
            Write-Host "The two games disagree on the checksum at frame ${Frames}: $($checksums -join ' vs ')"
        }
        Write-Host ("ONLINE SMOKE " + $(if ($ok) { 'PASS' } else { "FAIL (logs in $logDir)" }))
        if ($ok) { Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue }
        exit $(if ($ok) { 0 } else { 1 })
    }
}
