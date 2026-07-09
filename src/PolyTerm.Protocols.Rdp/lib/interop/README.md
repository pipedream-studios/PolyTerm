# MSTSC ActiveX interop assemblies

`PolyTerm.Protocols.Rdp.csproj` references two generated interop assemblies, committed in this
folder rather than restored via NuGet since they're generated from a DLL that ships with Windows
itself (`mstscax.dll`), not published anywhere. Regenerate with:

```
AxImp.exe C:\Windows\System32\mstscax.dll /source
```

using the .NET Framework 4.8 SDK tools (`AxImp.exe`/`TlbImp.exe`, typically under
`C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\`) on a machine where the
`MsTscAx.MsTscAx` control is registered (it ships with Windows — no separate install needed). This
produces `MSTSCLib.dll` and `AxMSTSCLib.dll` (plus a `.cs` source dump and `.pdb`, both discarded —
only the two DLLs are referenced via `HintPath`). Copy the two DLLs into this folder and commit them
if regenerating (e.g. after a Windows update changes the registered control version — check
`HKLM:\SOFTWARE\Classes\MsTscAx.MsTscAx` for the highest registered version and target that one).
