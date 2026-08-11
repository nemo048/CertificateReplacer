# CertReplacer

A small native Windows GUI app (Avalonia / .NET 10) that replaces certificate files across
subfolders. It's a port of a PowerShell `Replace-CertificatesInSubfolders` function to a
self-contained `.exe` — no PowerShell, no .NET runtime install required on the target machine.

## What it does

Given a root folder and a new certificate file, it walks every subfolder under the root. In any
subfolder that already contains files matching the configured certificate patterns
(`*.pfx`, `*.p12`, `*.cer*`, `*.crt`, `*.pem` by default), it removes those files and installs a
copy of the new certificate (keeping the new certificate's original file name). Folders with no
existing certificate files are left untouched.

Folder exclusion supports:
- an exact folder name (e.g. `00`)
- a relative or absolute path (e.g. `archive\logs`, `C:\certs\00`)
- a wildcard mask (e.g. `9*`, `archive\*`)

## Backup

When **Backup before replacing** is checked (on by default), every certificate file that's about
to be removed or overwritten is copied first to:

```
{backup folder}\{yyyyMMddHHmmss}\{root folder name}\{path relative to root}
```

For example, replacing certificates under `F:\bin\PFR\036` produces backups like
`backup\20260811143000\036\old.pfx` and `backup\20260811143000\036\sub\old.pfx`. The backup
folder defaults to a `backup` folder next to `CertReplacer.exe`, and can be changed in the UI.
Dry runs never write backups.

## Download

Grab the latest build from the [Releases page](../../releases) — `CertReplacer.exe` is
self-contained (includes the .NET runtime), just download and run.

## Usage

1. Pick the root folder to scan and the new certificate file to install.
2. Adjust certificate patterns / exclude folders if needed.
3. Leave **Dry run** checked and click **Run** to preview what would change.
4. Uncheck **Dry run**, click **Run**, and confirm to actually replace the certificates.

## Building locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish CertReplacer/CertReplacer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Running the tests

`CertReplacer.Tests` is a small console-based test suite covering the wildcard/folder-exclusion
matching and end-to-end replace behavior (dry-run, read-only files, excluded folders):

```
dotnet run --project CertReplacer.Tests -c Release
```

## Releases

Every push to `main` automatically builds a self-contained `win-x64` single-file exe and
publishes it to the rolling **[latest](../../releases/tag/latest)** pre-release, so
`CertReplacer.exe` there always matches the newest code. Pushing a `v*.*.*` tag (e.g. `v1.0.0`)
instead publishes a proper versioned release. You can also trigger a build manually from the
Actions tab (workflow_dispatch).
