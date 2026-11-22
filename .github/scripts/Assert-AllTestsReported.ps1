<#
.SYNOPSIS
Fails when a test project's .trx is missing results for tests that discovery found (issue #223).

.DESCRIPTION
On run 44978481706, PolyTerm.FlaUI.Tests reported 20 results out of its 21 tests: one test had no
result at all (not failed, skipped or "not executed"), and `dotnet test` still exited 0. This
script lists the project's tests with `dotnet test --list-tests`, reads the .trx that the test step
wrote, and exits 1 naming every listed test with no result (and any result for a test discovery
didn't list).

Tests are matched by display name, which is what both `--list-tests` and the .trx's testName hold.
Names are counted, not just compared as a set, so a theory with two rows that share a display name
can't hide one of them. If the two ever stop agreeing on format, every test shows as missing and
the step fails loudly: a parsing problem can't make it pass.

.PARAMETER Project
The test project (path to its folder or .csproj), already built with -Configuration.

.PARAMETER TrxPath
The .trx file the test step wrote for that project.

.PARAMETER Configuration
The build configuration to list tests from. Defaults to Release, matching CI.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $TrxPath,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TrxPath)) {
    Write-Output "::error::No .trx at '$TrxPath': the test step wrote no results at all for $Project."
    exit 1
}

# --- Discovered tests --------------------------------------------------------------------------
$listOutput = & dotnet test $Project -c $Configuration --no-restore --no-build --list-tests 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    $listOutput | Write-Output
    Write-Output "::error::'dotnet test --list-tests' failed for $Project (exit $LASTEXITCODE)."
    exit 1
}

# Everything after the "The following Tests are available:" header, one 4-space-indented display
# name per line (theory rows each get their own line).
$headerIndex = -1
for ($i = 0; $i -lt $listOutput.Count; $i++) {
    if ($listOutput[$i] -match '^The following Tests are available:') { $headerIndex = $i; break }
}
if ($headerIndex -lt 0) {
    $listOutput | Write-Output
    Write-Output "::error::Couldn't find the test list in 'dotnet test --list-tests' output for $Project."
    exit 1
}
$listed = @($listOutput[($headerIndex + 1)..($listOutput.Count - 1)] |
    Where-Object { $_ -match '^    \S' } |
    ForEach-Object { $_.Substring(4).TrimEnd() })
if ($listed.Count -eq 0) {
    $listOutput | Write-Output
    Write-Output "::error::'dotnet test --list-tests' listed no tests for $Project."
    exit 1
}

# --- Reported results --------------------------------------------------------------------------
[xml] $trx = Get-Content -LiteralPath $TrxPath -Raw
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$reported = @(Select-Xml -Xml $trx -XPath '//t:Results/t:UnitTestResult' -Namespace $ns |
    ForEach-Object { $_.Node.testName })

# --- Compare, counting duplicates --------------------------------------------------------------
function Get-NameCounts([string[]] $names) {
    $counts = @{}
    foreach ($n in $names) { $counts[$n] = 1 + [int] $counts[$n] }
    $counts
}
$listedCounts = Get-NameCounts $listed
$reportedCounts = Get-NameCounts $reported

$missing = foreach ($name in $listedCounts.Keys) {
    $short = $listedCounts[$name] - [int] $reportedCounts[$name]
    for ($i = 0; $i -lt $short; $i++) { $name }
}
$unexpected = foreach ($name in $reportedCounts.Keys) {
    $extra = $reportedCounts[$name] - [int] $listedCounts[$name]
    for ($i = 0; $i -lt $extra; $i++) { $name }
}
$missing = @($missing | Sort-Object)
$unexpected = @($unexpected | Sort-Object)

Write-Output "$Project`: $($listed.Count) test(s) discovered, $($reported.Count) result(s) in $(Split-Path -Leaf $TrxPath)."

if ($missing.Count -eq 0 -and $unexpected.Count -eq 0) {
    Write-Output 'Every discovered test has exactly one result.'
    exit 0
}

foreach ($name in $missing) {
    Write-Output "::error title=Test result missing::$name was discovered but has no result in the .trx (issue #223)."
}
foreach ($name in $unexpected) {
    Write-Output "::error title=Unexpected test result::$name has a result in the .trx but wasn't in the discovered list."
}
exit 1
