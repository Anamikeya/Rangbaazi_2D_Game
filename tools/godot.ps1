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

  Don't use the "godot" alias that winget adds: it is a symlink, so Godot looks for its
  .NET assemblies next to the link, fails with ".NET: Assemblies not found" and crashes.
  This script finds the real executable. To override, set $env:GODOT to Godot's
  *_console.exe (a symlink or the GUI exe next to it also works).

.EXAMPLE
  ./tools/godot.ps1
  ./tools/godot.ps1 smoke
  ./tools/godot.ps1 record -Out "$env:TEMP\smoke.avi"
#>
param(
    [ValidateSet('editor', 'run', 'smoke', 'record')]
    [string]$Mode = 'editor',
    [string]$Out = ''
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
}
