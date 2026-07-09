#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a PolyTerm.msi installer (M5 packaging) using the WiX Toolset v5+ CLI.

.DESCRIPTION
    Publishes PolyTerm.App and PolyTerm.Cli self-contained (win-x64) into a staging folder (same
    App-root / cli-subfolder layout as build-portable-zip.ps1, for the same reason: PolyTerm.exe and
    polyterm.exe collide on Windows' case-insensitive filesystem if published together), then runs
    `wix build` against packaging/windows/msi/PolyTerm.wxs, which harvests every published file via a
    glob (no per-file list to maintain).

    Requires the `wix` global dotnet tool: `dotnet tool install --global wix`.

    WiX Toolset v7's "Open Source Maintenance Fee" EULA (https://wixtoolset.org/osmf/ - $10/month,
    waived for anyone earning under $10,000/year from the software) is accepted here via
    `wix eula accept wix7` - a one-time, persistent acceptance (writes a marker under the invoking
    user's profile), not a per-build flag (`--acceptEula` alone, without a value, is NOT how this
    works despite the name suggesting a switch - confirmed by running it: WIX0251 "expected to be
    followed by a value"; the real mechanism is the `eula accept <eulaId>` subcommand). Deliberate,
    explicit, and reviewed with the user before being added here (2026-07-09); not a default anyone
    should assume is fine to copy into a different project without the same conversation.

.PARAMETER SmokeTest
    After building the MSI, extracts it with msiexec's /a (administrative install - unpacks the MSI's
    contents to a folder without touching the real system: no registry writes, no Start Menu/Desktop
    shortcuts, no Program Files entry) and confirms PolyTerm.exe is present and launches. This is
    deliberately NOT a real install/uninstall cycle, which would modify this machine's actual installed
    state - see the script's own final Write-Host for what that means for verification confidence.

.EXAMPLE
    ./build-msi.ps1 -SmokeTest
#>
param(
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path "$PSScriptRoot/../.."
$stageDir = Join-Path $PSScriptRoot "msi-stage"
$cliDir = Join-Path $stageDir "cli"
$outDir = Join-Path $PSScriptRoot "publish/msi"

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    throw "The 'wix' CLI isn't on PATH. Install it with: dotnet tool install --global wix"
}

Write-Host "== Accepting the WiX Toolset OSMF EULA (wix7) - see this script's own doc comment =="
wix eula accept wix7
if ($LASTEXITCODE -ne 0) { throw "wix eula accept failed." }

Write-Host "== Reading version from Directory.Build.props =="
$version = dotnet msbuild "$repoRoot/src/PolyTerm.App/PolyTerm.App.csproj" -getProperty:Version -nologo
# MSI ProductVersion must be a plain N.N.N(.N) - no prerelease/metadata suffix like InformationalVersion has.
Write-Host "Version: $version"

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $cliDir -Force | Out-Null
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "== Publishing PolyTerm.App (win-x64, self-contained) =="
dotnet publish "$repoRoot/src/PolyTerm.App/PolyTerm.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "PolyTerm.App publish failed." }

Write-Host "== Publishing PolyTerm.Cli (win-x64, self-contained) into cli/ =="
dotnet publish "$repoRoot/src/PolyTerm.Cli/PolyTerm.Cli.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $cliDir
if ($LASTEXITCODE -ne 0) { throw "PolyTerm.Cli publish failed." }

# .pdb files aren't useful in a shipped installer and just bloat it - strip them from the staged
# copy only (never touch the real publish/bin output the rest of the build relies on).
Get-ChildItem -Path $stageDir -Filter "*.pdb" -Recurse | Remove-Item -Force

$msiPath = Join-Path $outDir "PolyTerm-$version-win-x64.msi"
Write-Host "== Building $msiPath with wix =="
wix build "$PSScriptRoot/msi/PolyTerm.wxs" `
    -d "ProductVersion=$version" `
    -d "PublishDir=$stageDir" `
    -arch x64 `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed." }

if ($SmokeTest) {
    Write-Host "== Smoke-testing via administrative extract (msiexec /a - no real install) =="
    $extractDir = Join-Path $outDir "extract-test"
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    $msiArgs = @("/a", "`"$msiPath`"", "/qn", "TARGETDIR=`"$extractDir`"")
    $proc = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a extraction failed with exit code $($proc.ExitCode)." }

    $extractedExe = Get-ChildItem -Path $extractDir -Filter "PolyTerm.exe" -Recurse | Select-Object -First 1
    if (-not $extractedExe) { throw "PolyTerm.exe not found anywhere under the extracted MSI contents." }
    Write-Host "Found extracted PolyTerm.exe at: $($extractedExe.FullName)"

    $runProc = Start-Process -FilePath $extractedExe.FullName -PassThru
    Start-Sleep -Seconds 4
    if ($runProc.HasExited) {
        throw "Extracted PolyTerm.exe exited early (code $($runProc.ExitCode))."
    }
    Write-Host "Extracted PolyTerm.exe is running (PID $($runProc.Id)) - looks healthy. Stopping it."
    Stop-Process -Id $runProc.Id -Force

    Write-Host ""
    Write-Host "NOTE: this proves the MSI's payload is correct and launchable. It does NOT prove a" -ForegroundColor Yellow
    Write-Host "real end-user install/uninstall cycle (Start Menu/Desktop shortcuts, Add/Remove" -ForegroundColor Yellow
    Write-Host "Programs entry, upgrade behavior) works - that needs an actual msiexec /i, which" -ForegroundColor Yellow
    Write-Host "modifies real system state and was deliberately not done without explicit sign-off." -ForegroundColor Yellow
}

$sizeMb = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Host "== Done: $msiPath ($sizeMb MB) =="
