<#
.SYNOPSIS
Fails when a test project's .trx is missing results for tests that discovery found (issues #223, #303).

.DESCRIPTION
On run 44978481706, PolyTerm.FlaUI.Tests reported 20 results out of its 21 tests: one test had no
result at all (not failed, skipped or "not executed"), and `dotnet test` still exited 0. This
script lists each project's tests with `dotnet test --list-tests`, reads the .trx that the test step
wrote for it, and exits 1 naming every listed test with no result (and any result for a test
discovery didn't list).

It has two modes:

- One project and its .trx (-Project, -TrxPath): for a test step that runs a single project with a
  fixed LogFileName, like FlaUI.Tests on Windows.
- Every test project in a solution (-Solution, -ResultsDirectory): for a `dotnet test <solution>
  --logger trx` step. Each project gets VSTest's default timestamped .trx name, so each .trx is
  mapped back to its project through the test assembly its results came from (the `storage`
  attribute of its UnitTest entries). A project with no .trx, or whose .trx holds no results, fails:
  that is what a run that lost all of its results looks like. -Exclude drops projects a separate
  step covers (FlaUI.Tests on Windows).

Tests are matched by display name, which is what both `--list-tests` and the .trx's testName hold.
Names are counted, not just compared as a set, so a theory with two rows that share a display name
can't hide one of them. A theory whose data can't be enumerated at discovery is listed once, by its
bare name, and reports one result per row as `Name(args)`; those rows count as that one listed test.
If the two ever stop agreeing on format, every test shows as missing and the step fails loudly: a
parsing problem can't make it pass.

.PARAMETER Project
Single-project mode: the test project (path to its folder or .csproj), already built with
-Configuration.

.PARAMETER TrxPath
Single-project mode: the .trx file the test step wrote for that project.

.PARAMETER Solution
Solution mode: the .slnx whose test projects (those under tests/) to check, already built with
-Configuration.

.PARAMETER ResultsDirectory
Solution mode: the folder the test step wrote its .trx files to (its --results-directory).

.PARAMETER Exclude
Solution mode: project names (e.g. PolyTerm.FlaUI.Tests) to leave out because another step runs and
checks them.

.PARAMETER Configuration
The build configuration to list tests from. Defaults to Release, matching CI.
#>
[CmdletBinding(DefaultParameterSetName = 'Single')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Single')] [string] $Project,
    [Parameter(Mandatory, ParameterSetName = 'Single')] [string] $TrxPath,
    [Parameter(Mandatory, ParameterSetName = 'Solution')] [string] $Solution,
    [Parameter(Mandatory, ParameterSetName = 'Solution')] [string] $ResultsDirectory,
    [Parameter(ParameterSetName = 'Solution')] [string[]] $Exclude = @(),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$trxNs = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

# Returns the display names `dotnet test --list-tests` prints for a project, or $null after
# writing an ::error:: if it couldn't list them.
function Get-ListedTests([string] $ProjectPath) {
    $listOutput = & dotnet test $ProjectPath -c $Configuration --no-restore --no-build --list-tests 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        $listOutput | Write-Host
        Write-Host "::error::'dotnet test --list-tests' failed for $ProjectPath (exit $LASTEXITCODE)."
        return $null
    }

    # Everything after the "The following Tests are available:" header, one 4-space-indented
    # display name per line (theory rows each get their own line).
    $headerIndex = -1
    for ($i = 0; $i -lt $listOutput.Count; $i++) {
        # VSTest: "The following Tests are available:"; MTP (#304 spike): "Discovered N tests in assembly - ...".
        if ($listOutput[$i] -match '^(The following Tests are available:|Discovered \d+ tests in assembly)') { $headerIndex = $i; break }
    }
    if ($headerIndex -lt 0) {
        $listOutput | Write-Host
        Write-Host "::error::Couldn't find the test list in 'dotnet test --list-tests' output for $ProjectPath."
        return $null
    }
    $listed = @($listOutput[($headerIndex + 1)..($listOutput.Count - 1)] |
        Where-Object { $_ -match '^ {2,}\S' } |
        ForEach-Object { $_.Trim() })
    if ($listed.Count -eq 0) {
        $listOutput | Write-Host
        Write-Host "::error::'dotnet test --list-tests' listed no tests for $ProjectPath."
        return $null
    }
    , $listed
}

function Get-ReportedTests([xml] $Trx) {
    , @(Select-Xml -Xml $Trx -XPath '//t:Results/t:UnitTestResult' -Namespace $trxNs |
        # xUnit's own MTP .trx writer (--report-xunit-trx) escapes a quote inside theory arguments
        # as \" where --list-tests prints a plain " (#304 spike); undo that so the names match.
        ForEach-Object { $_.Node.testName -replace '\\"', '"' })
}

function Get-NameCounts([string[]] $names) {
    $counts = @{}
    foreach ($n in $names) { $counts[$n] = 1 + [int] $counts[$n] }
    $counts
}

# Compares one project's listed tests with its .trx results. Writes a summary line and an
# ::error:: per gap (to the host, so the return value stays a bool); returns $true when every
# listed test has a result.
function Test-ProjectReported([string] $ProjectPath, [string[]] $Listed, [string[]] $Reported, [string] $TrxName) {
    $listedCounts = Get-NameCounts $Listed
    $reportedCounts = Get-NameCounts $Reported

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $listedCounts.Keys) {
        $short = $listedCounts[$name] - [int] $reportedCounts[$name]
        for ($i = 0; $i -lt $short; $i++) { $missing.Add($name) }
    }
    $unexpected = @{}
    foreach ($name in $reportedCounts.Keys) {
        $extra = $reportedCounts[$name] - [int] $listedCounts[$name]
        if ($extra -gt 0) { $unexpected[$name] = $extra }
    }

    # A theory listed once by its bare name (data not enumerable at discovery) that reported its
    # rows as "Name(args)": the rows stand for that one listed test.
    foreach ($name in @($missing)) {
        $rows = @($unexpected.Keys | Where-Object { $_.StartsWith("$name(", [StringComparison]::Ordinal) })
        if ($rows.Count -gt 0) {
            [void] $missing.Remove($name)
            foreach ($row in $rows) { $unexpected.Remove($row) }
        }
    }

    Write-Host "${ProjectPath}: $($Listed.Count) test(s) discovered, $($Reported.Count) result(s) in $TrxName."

    if ($missing.Count -eq 0 -and $unexpected.Count -eq 0) {
        Write-Host '  Every discovered test has a result.'
        return $true
    }
    foreach ($name in ($missing | Sort-Object)) {
        Write-Host "::error title=Test result missing::$name was discovered but has no result in the .trx (issue #223)."
    }
    foreach ($name in ($unexpected.Keys | Sort-Object)) {
        for ($i = 0; $i -lt $unexpected[$name]; $i++) {
            Write-Host "::error title=Unexpected test result::$name has a result in the .trx but wasn't in the discovered list."
        }
    }
    return $false
}

# --- Single project ----------------------------------------------------------------------------
if ($PSCmdlet.ParameterSetName -eq 'Single') {
    if (-not (Test-Path -LiteralPath $TrxPath)) {
        Write-Host "::error::No .trx at '$TrxPath': the test step wrote no results at all for $Project."
        exit 1
    }
    $listed = Get-ListedTests $Project
    if ($null -eq $listed) { exit 1 }
    [xml] $trx = Get-Content -LiteralPath $TrxPath -Raw
    $ok = Test-ProjectReported $Project $listed (Get-ReportedTests $trx) (Split-Path -Leaf $TrxPath)
    exit ($ok ? 0 : 1)
}

# --- Every test project in a solution ----------------------------------------------------------
$solutionDir = Split-Path -Parent (Resolve-Path -LiteralPath $Solution)
[xml] $slnx = Get-Content -LiteralPath $Solution -Raw
$projects = @($slnx.SelectNodes('//Project') |
    ForEach-Object { $_.GetAttribute('Path') } |
    Where-Object { $_ -match '^tests[\\/]' } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } |
    Where-Object { $_ -notin $Exclude })
if ($projects.Count -eq 0) {
    Write-Host "::error::Found no test projects under tests/ in $Solution."
    exit 1
}

# Map each .trx to the test assembly its results came from. Assembly names are the project names
# (no test project sets AssemblyName). A .trx with no results maps to nothing, so its project shows
# as having no .trx below.
$trxByAssembly = @{}
$trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -ErrorAction SilentlyContinue)
foreach ($file in $trxFiles) {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
    $assemblies = @(Select-Xml -Xml $trx -XPath '//t:TestDefinitions/t:UnitTest' -Namespace $trxNs |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension(($_.Node.storage -replace '\\', '/')) } |
        Sort-Object -Unique)
    foreach ($assembly in $assemblies) {
        $key = $assembly.ToLowerInvariant()
        if (-not $trxByAssembly.ContainsKey($key)) { $trxByAssembly[$key] = [System.Collections.Generic.List[object]]::new() }
        $trxByAssembly[$key].Add(@{ File = $file; Trx = $trx })
    }
}

$failed = 0
foreach ($name in $projects) {
    $entries = $trxByAssembly[$name.ToLowerInvariant()]
    if ($null -eq $entries) {
        Write-Host "::error::No .trx in '$ResultsDirectory' holds results from ${name}: the test step wrote no results at all for it."
        $failed++
        continue
    }
    if ($entries.Count -gt 1) {
        Write-Host "::error::More than one .trx in '$ResultsDirectory' holds results from $name ($(($entries | ForEach-Object { $_.File.Name }) -join ', ')); expected one per project."
        $failed++
        continue
    }
    $listed = Get-ListedTests (Join-Path $solutionDir "tests/$name")
    if ($null -eq $listed) { $failed++; continue }
    if (-not (Test-ProjectReported "tests/$name" $listed (Get-ReportedTests $entries[0].Trx) $entries[0].File.Name)) { $failed++ }
}

if ($failed -gt 0) {
    Write-Host "::error::$failed of $($projects.Count) test project(s) have tests with no result, or no .trx."
    exit 1
}
Write-Host "All $($projects.Count) test project(s): every discovered test has a result."
exit 0
