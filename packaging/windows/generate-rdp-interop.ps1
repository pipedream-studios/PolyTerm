#Requires -Version 5.1
<#
Regenerates the two MSTSC ActiveX interop assemblies PolyTerm.Protocols.Rdp needs
(MSTSCLib.dll, AxMSTSCLib.dll) from the copy of mstscax.dll that ships with Windows.

These are deliberately NOT committed to the repo - they're derived from a Microsoft-owned
DLL, not something we have the right to redistribute in a public repo. See
src/PolyTerm.Protocols.Rdp/lib/interop/README.md for the full reasoning. CI (and any Windows
build machine building PolyTerm.Protocols.Rdp for the first time) runs this script instead.
#>

$ErrorActionPreference = "Stop"

$interopDir = Join-Path $PSScriptRoot "..\..\src\PolyTerm.Protocols.Rdp\lib\interop" | Resolve-Path
$axImp = Get-ChildItem "C:\Program Files (x86)\Microsoft SDKs\Windows" -Recurse -Filter AxImp.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $axImp) {
    throw "AxImp.exe not found under C:\Program Files (x86)\Microsoft SDKs\Windows - this build machine is missing the .NET Framework 4.8 SDK tools (NETFX 4.8 Tools). See src/PolyTerm.Protocols.Rdp/lib/interop/README.md."
}

Write-Host "Using $axImp"
Push-Location $interopDir
try {
    & $axImp C:\Windows\System32\mstscax.dll /source
    if ($LASTEXITCODE -ne 0) {
        throw "AxImp.exe exited with code $LASTEXITCODE"
    }
    # /source also dumps generated .cs and .pdb files - only the two DLLs are referenced via HintPath.
    Remove-Item *.cs, *.pdb -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}

if (-not (Test-Path (Join-Path $interopDir "MSTSCLib.dll")) -or -not (Test-Path (Join-Path $interopDir "AxMSTSCLib.dll"))) {
    throw "AxImp.exe ran but did not produce both MSTSCLib.dll and AxMSTSCLib.dll in $interopDir"
}

Write-Host "Generated MSTSCLib.dll and AxMSTSCLib.dll in $interopDir"
