# Releasing Kurobane

A push of a tag `vMAJOR.MINOR.PATCH` (for example `v0.5.0`) runs `.github/workflows/release.yml` on `windows-latest`.
The workflow checks out that tag, builds the self-contained zip `Kurobane-win-x64.zip` and the per-user installer
`Kurobane-Setup-X.Y.Z.exe`, signs the zip with the repository secret, verifies the signature against the public key in
`UpdateTrust.PublisherPublicKeySpkiBase64`, and publishes a normal GitHub Release (not a draft, not a pre-release,
marked latest). The in-app updater reads that release.

The private key is not stored in the repository. The workflow writes it to a temp file, never prints it, and deletes
the file even when a later step fails.

## One-time setup

1. Generate the publisher keypair on a PC **outside** the repo (the tool refuses to write inside a checkout):

   ```powershell
   dotnet run --project tools/Kurobane.ReleaseTool -- keygen --out D:\keys\kurobane-release.pem
   ```

   Back the key up yourself. Paste the printed **public** key into
   `src/Kurobane.Core/Updates/ReleaseSigning.cs` → `UpdateTrust.PublisherPublicKeySpkiBase64`, rebuild, and ship that
   build once by hand (the installer), so the key is embedded before the first signed release.
2. Open the repository on GitHub → **Settings → Secrets and variables → Actions → New repository secret**.
3. Name it exactly `KUROBANE_RELEASE_KEY_PEM` and paste the full PEM (including begin/end lines).

The workflow stops with a clear error if the secret is missing or not a PEM private key, and publishes nothing. If
the secret is a different key, signature verification fails and nothing is published.

## Cut a release

1. Bump `<Version>` in `Directory.Build.props` to the same number.
2. Add plain-text notes at `docs/release-notes/vX.Y.Z.txt` (the updater shows them; short sentences, `-` lists).
3. Commit and push `main`.
4. Tag that commit and push the tag: `git tag vX.Y.Z` then `git push origin vX.Y.Z`.

The tag push starts the Release workflow. When it finishes,
`https://github.com/BookahCoding/Kurobane/releases/latest` has `Kurobane-Setup-X.Y.Z.exe`, `Kurobane-win-x64.zip`,
and `Kurobane-win-x64.zip.sig`.

## Publish a tag that already exists

Open **Actions → Release → Run workflow** on the `main` branch and set the tag input to `vX.Y.Z`, or from a machine
logged into GitHub: `gh workflow run release.yml --ref main -f tag=vX.Y.Z`.
