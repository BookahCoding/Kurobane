# GitHub Releases updater

**Status:** implemented and fixture-tested (synthetic keys/zips/handlers in `tests/Kurobane.Tests/UpdaterTests.cs`; no
real release and no real publisher key yet). The publisher public key is **not embedded yet**:
`UpdateTrust.PublisherPublicKeySpkiBase64` in `src/Kurobane.Core/Updates/ReleaseSigning.cs` is empty until the owner
runs `keygen` and pastes the public key — until then the app still *checks* for updates and shows the release page,
but **Download and verify** is disabled (fail closed). The app version starts at **0.5.0** (`Directory.Build.props`).

## What the user sees (Settings → Updates)

1. **Check for updates.** Anonymous `GET /repos/BookahCoding/Kurobane/releases/latest`. Also run **once at start-up**
   (5 s after the window opens, 20 s timeout, silent on any failure; Settings → Updates → "Check for a new version
   when Kurobane starts", on by default): if a newer release exists, an "Update to x.y.z" banner appears at the top
   of the window and opens this section with a fresh check. The start-up check never downloads anything.
2. Current version, latest version and plain-text release notes (control characters stripped, 4000 characters max).
3. **Download and verify** — enabled only when a publisher key is embedded. Explicit click; cancellable.
4. **Install and restart** — a second, separate confirmation (default answer No). No downgrades are ever offered.
5. **Open release page** — opens the GitHub release page for that tag in the browser (checked against this repo and tag).

## Trust model (why a hash is not enough)

A SHA-256 published next to a download proves nothing: whoever can replace the zip can replace the hash. The updater
therefore trusts only a **project signing key**: ECDSA P-256 + SHA-256, detached signature `Kurobane-win-x64.zip.sig`
(base64, 64-byte IEEE-P1363). The public key is compiled into the app; the private key is generated and kept by the
owner offline and never enters this repository.

The signed message is `kurobane-release-v1 \n platform=win-x64 \n version=X.Y.Z \n sha256=<zip hash>`. Because the
version and platform are signed, a validly signed *older* package cannot be presented as a newer release
(rollback / mix-up).

**Fail closed:** no embedded key (or an unusable one) → nothing is downloaded and nothing is run. Bad, malformed or
mismatched signature → the download is deleted and never extracted or executed.

## Download and extraction safety

- HTTPS only; hosts limited to `api.github.com`, `github.com`, `objects.githubusercontent.com`,
  `release-assets.githubusercontent.com`; redirects followed manually, each hop re-checked, at most 5; no credentials sent.
- Asset URLs must be exactly `https://github.com/<repo>/releases/download/<tag>/<name>`; the JSON's claim alone is not trusted.
- Only strict `vMAJOR.MINOR.PATCH` stable releases (no draft/pre-release) with both the zip and the signature asset.
- Size bounds: 150 MB package, 4 KB signature, 512 KB API answer; partial files removed on every failure.
- Extraction: rejects `..`, rooted/UNC/drive/stream (`:`) names, reserved device names, trailing dot/space,
  duplicates, missing root `Kurobane.exe`, too many entries, and bounds the *bytes actually written* (not declared sizes).
  It extracts to a temporary folder and renames, so a failure leaves nothing.

## Install (transactional, no standing backups)

The app starts the **already-verified new build** from the staging folder as a small helper (`--apply-update`), then
exits. The helper waits for the old process to end (60 s, then refuses), moves each file it replaces into a transient
`.update-old` folder, copies the new files, and on any failure restores everything; on success the transient folder is
deleted. This is **not** an app backup. A crash mid-apply is recovered at the next apply. Files in the install folder
that the new package does not contain are left alone. `%APPDATA%\Kurobane` (settings, DPAPI secrets) is never touched.
If the install folder is not writable (e.g. Program Files without elevation) the app says so and points at the release
page instead. Prepared packages live in `%LOCALAPPDATA%\Kurobane\updates` and are deleted at the next normal start; a
prepared update is never installed automatically.

## First install: the per-user installer

`installer/Kurobane.iss` (Inno Setup 6) installs into `%LOCALAPPDATA%\Programs\Kurobane` — no administrator rights —
so the in-app updater can write the install folder (it cannot update a copy under Program Files). The uninstaller
removes the program folder and the updater's work folder but never `%APPDATA%\Kurobane` (settings and the DPAPI-sealed
token are kept). Built by the Release workflow as `Kurobane-Setup-X.Y.Z.exe`, or by hand:

```powershell
./scripts/package.ps1 -Version v0.5.0 -KeyPath D:\keys\kurobane-release.pem -Installer
```

## Owner procedure (you hold the key)

See [RELEASING.md](RELEASING.md). Release assets must be named exactly `Kurobane-win-x64.zip` and
`Kurobane-win-x64.zip.sig`; the setup exe is for first installs and is ignored by the updater.

## Honest limits

- Private repositories answer 401/403/404 to the anonymous check and are reported as such.
- Authenticode / SmartScreen reputation is separate and optional; it would not replace the signing key.
- A same-user local attacker who can write the staging folder after verification is out of scope (they could already
  write the install folder).
