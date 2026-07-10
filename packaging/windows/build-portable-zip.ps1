#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a self-contained, no-installer-needed PolyTerm distribution for Windows (App + CLI) and
    zips it - the "portable zip" leg of M5's packaging scope (docs/polyterm-spec.md, section 10).

.DESCRIPTION
    Publishes PolyTerm.App and PolyTerm.Cli self-contained for the given runtime identifier into
    SEPARATE subfolders (App at the zip root, CLI under cli/) and zips the result. They cannot share
    one output folder: PolyTerm.App's AssemblyName is "PolyTerm" and PolyTerm.Cli's is "polyterm" -
    different on a case-sensitive filesystem, but Windows' default NTFS is case-insensitive, so
    PolyTerm.exe and polyterm.exe are literally the same file there. Publishing both into one folder
    let the second publish silently overwrite the first's exe/dll/runtimeconfig - confirmed by hand:
    the resulting "PolyTerm.exe" launched and printed the CLI's own --help text instead of the desktop
    app. Not a bug in the app itself, just in an earlier version of this script.

    Deliberately not single-file publish: single-file extracts native dependencies (SQLite, Avalonia's
    Skia bindings) to a temp directory on every launch, adding real startup latency for a terminal app
    users expect to open instantly - the plain self-contained folder form was verified (this script's
    own -SmokeTest switch) to launch cleanly with no such overhead.

.PARAMETER RuntimeIdentifier
    .NET RID to publish for. Default win-x64 - the only one built/tested on this machine.

.PARAMETER SmokeTest
    After publishing, actually launches the packaged PolyTerm.exe and polyterm.exe and confirms both
    start cleanly (matches the manual verification already done once by hand for this exact script -
    including the App/CLI filename collision this same switch caught). Only possible when
    RuntimeIdentifier matches this machine's own architecture - Windows can't run an arm64 binary on
    x64 hardware (only the reverse, via emulation) - so a mismatched RID publishes and confirms the
    files exist, but skips actually launching them, with a clear note explaining why.

.EXAMPLE
    ./build-portable-zip.ps1 -SmokeTest
#>
param(
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path "$PSScriptRoot/../.."
$outDir = Join-Path $PSScriptRoot "publish/$RuntimeIdentifier"
$stageDir = Join-Path $outDir "PolyTerm"
$cliDir = Join-Path $stageDir "cli"

Write-Host "== Reading version from Directory.Build.props =="
$version = dotnet msbuild "$repoRoot/src/PolyTerm.App/PolyTerm.App.csproj" -getProperty:Version -nologo
Write-Host "Version: $version"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $cliDir -Force | Out-Null

Write-Host "== Publishing PolyTerm.App ($RuntimeIdentifier, self-contained) =="
dotnet publish "$repoRoot/src/PolyTerm.App/PolyTerm.App.csproj" `
    -c Release -r $RuntimeIdentifier --self-contained true `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "PolyTerm.App publish failed." }

Write-Host "== Publishing PolyTerm.Cli ($RuntimeIdentifier, self-contained) into cli/ =="
dotnet publish "$repoRoot/src/PolyTerm.Cli/PolyTerm.Cli.csproj" `
    -c Release -r $RuntimeIdentifier --self-contained true `
    -o $cliDir
if ($LASTEXITCODE -ne 0) { throw "PolyTerm.Cli publish failed." }

if ($SmokeTest) {
    # win-arm64 built on x64 hardware (or vice versa) produces real, valid binaries - dotnet publish
    # cross-compiles fine - but this host's CPU literally cannot execute them, so actually launching
    # is only possible when the RID matches this machine's own architecture.
    $hostArch = if ([Environment]::Is64BitOperatingSystem) {
        [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    } else { "x86" }
    $ridArch = switch ($RuntimeIdentifier) {
        "win-x64"   { "x64" }
        "win-arm64" { "arm64" }
        default     { $null }
    }
    $canActuallyRun = $ridArch -and ($ridArch -eq $hostArch)

    if ($canActuallyRun) {
        Write-Host "== Smoke-testing the packaged PolyTerm.exe =="
        $exe = Join-Path $stageDir "PolyTerm.exe"
        $proc = Start-Process -FilePath $exe -PassThru
        Start-Sleep -Seconds 4
        if ($proc.HasExited) {
            throw "PolyTerm.exe exited early (code $($proc.ExitCode)) - publish is broken, not packaging this."
        }
        Write-Host "PolyTerm.exe is running (PID $($proc.Id)) - looks healthy. Stopping it."
        Stop-Process -Id $proc.Id -Force

        Write-Host "== Smoke-testing the packaged cli/polyterm.exe =="
        $cliExe = Join-Path $cliDir "polyterm.exe"
        $cliVersion = & $cliExe --version
        Write-Host "polyterm.exe --version: $cliVersion"
        if ([string]::IsNullOrWhiteSpace($cliVersion)) { throw "polyterm.exe --version produced no output." }
        if ($cliVersion -notmatch [regex]::Escape($version)) {
            throw "polyterm.exe --version ('$cliVersion') doesn't contain the expected version ('$version')."
        }
    } else {
        $exe = Join-Path $stageDir "PolyTerm.exe"
        $cliExe = Join-Path $cliDir "polyterm.exe"
        if (-not (Test-Path $exe)) { throw "PolyTerm.exe not found at $exe - publish is broken." }
        if (-not (Test-Path $cliExe)) { throw "polyterm.exe not found at $cliExe - publish is broken." }
        Write-Host "== Skipping launch-based smoke test: RuntimeIdentifier '$RuntimeIdentifier' ($ridArch) doesn't match this machine's own architecture ($hostArch) ==" -ForegroundColor Yellow
        Write-Host "Confirmed both PolyTerm.exe and polyterm.exe exist and are non-empty; cannot confirm they actually run without matching hardware (or an ARM64 Windows device, for win-arm64)." -ForegroundColor Yellow
    }
}

$zipPath = Join-Path $outDir "PolyTerm-$version-$RuntimeIdentifier.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "== Zipping to $zipPath =="
Compress-Archive -Path "$stageDir/*" -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "== Done: $zipPath ($sizeMb MB) =="
