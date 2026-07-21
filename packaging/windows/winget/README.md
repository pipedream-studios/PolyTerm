# Winget manifest

Three files here follow the [winget-pkgs manifest schema](https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema/1.6.0)
for submitting PolyTerm to the community [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs)
repo, so `winget install chris3081.PolyTerm` works for anyone:

- `chris3081.PolyTerm.yaml` — version manifest
- `chris3081.PolyTerm.installer.yaml` — installer manifest (points at the win-x64 MSI)
- `chris3081.PolyTerm.locale.en-US.yaml` — default locale manifest (description, license, tags)

**These are not submittable yet.** `winget-pkgs` requires a real, downloadable release artifact and
its real SHA256 hash — this repo has never had a real tag pushed (see issue #10), so
`InstallerSha256` in the installer manifest is a literal placeholder, not a fake-real-looking value,
so nobody submits it by accident.

## To actually finish and submit this

1. Tag and push a real release (`v0.9.0` or whatever the version is by then) — this runs
   `.github/workflows/release.yml`, which builds, smoke-tests, and publishes
   `PolyTerm-<version>-win-x64.msi` as a real GitHub Release asset.
2. Compute the real installer hash from the published asset:
   ```powershell
   Get-FileHash "PolyTerm-<version>-win-x64.msi" -Algorithm SHA256
   ```
   (or download it fresh and hash that — don't hash a local build, the release artifact's bytes are
   what winget actually validates against.)
3. Update all three files here: `PackageVersion` (all three), `InstallerUrl`/`InstallerSha256`
   (installer manifest) — bump the version to match whatever was actually tagged.
4. Validate locally before submitting — either the
   [`winget-create`](https://github.com/microsoft/winget-create) tool
   (`wingetcreate update chris3081.PolyTerm --version <version> --urls <msi-url> --submit`, which
   handles the hash computation and PR automatically), or `winget validate --manifest .` if you have
   the manifest files staged in winget-pkgs' own expected directory layout
   (`manifests/c/chris3081/PolyTerm/<version>/`).
5. Submit as a PR to `microsoft/winget-pkgs` (or let `wingetcreate --submit` do it, which needs a
   GitHub token with permission to fork/PR that repo).

## If a win-arm64 release is ever published too

`packaging/windows/build-msi.ps1`/`build-portable-zip.ps1` already support
`-RuntimeIdentifier win-arm64` (see issue #18), but `release.yml` doesn't invoke them with it yet —
that's a separate, deliberate change to the actual release pipeline's output, not bundled into this.
If/when it is, add a second entry under `Installers:` in the installer manifest with
`Architecture: arm64` and its own `InstallerUrl`/`InstallerSha256`, matching the `x64` entry's shape.
