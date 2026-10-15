# Build and delivery

Run `build.bat --run` on 64-bit Windows to acquire pinned project-local tools, publish the real application, verify the payload, and launch it. `build.bat /s` is noninteractive and does not launch. `download-dependencies.bat /s` only acquires tools. `build-installer.bat /s` builds and creates the genuine unsigned Squirrel.Windows installer without installing or publishing it.

The portable bootstrap needs no administrator rights and does not change system PATH, security settings, or existing SDK installations. HTTPS downloads are pinned in `build/manifest.json` and verified before extraction. Extracted tool files are inventoried and checked before reuse. The bootstrap uses an exclusive lock, a durable activation journal, archive-derived file hashes, exact file inventory, path containment checks, and a preserved previous extraction. Interrupted or malformed journal recovery retains evidence and requires a verified active or backup tree. A missing or changed tool is reacquired from the pinned archive.

## Outputs and source binding

`artifacts/publish/WinSshCopyId.exe` is the runnable native WinUI 3 x64 executable. Both the .NET runtime and Windows App SDK runtime are bundled. `artifacts/releases/` contains `Setup.exe`, `RELEASES`, and the versioned full NuGet package. The installer is unsigned and can show an unknown-publisher warning. Build and installer receipts bind file hashes, manifest hash, version, runtime, and exact committed source. Builds reject staged, unstaged, and untracked source changes.

`BUILD_VERSION` optionally sets a numeric `major.minor.patch` version. The hosted job uses `1.0.<run number>`. Builds and packaging do not publish or install. Local runtime acceptance, installation, upgrade, cancellation, uninstall, rollback, and fresh-machine behavior require separate evidence.

## Hosted delivery

The only job uses the standard `windows-2022` hosted image. Every branch push and manual dispatch builds and packages through the root entrypoints. There are no test, lint, type-check, security scan, self-hosted, or paid-runner jobs. The job inventory is: portable .NET SDK 8.0.426, NuGet 6.14.0, Squirrel.Windows 2.0.1, project package references restored by the SDK, built-in Windows PowerShell and .NET Framework for packaging, and the hosted GitHub CLI for publication. Bootstrap is part of both root build paths and requires no warm SDK cache.

Every branch publishes a normal release after proving its candidate descends from main lineage. Only the exact main head is marked latest; task-branch releases use `--latest=false`. Unique immutable tags include the version, run ID, and attempt. The publisher validates receipts and asset hashes, creates a normal release, resolves the exact tag source, reads back draft/prerelease state and asset sizes, then downloads and hashes every published asset. Other branches also retain downloadable workflow outputs without changing the public update feed. Safe output collection and upload run even on failure with seven-day retention and never upload source, dependency caches, or credentials.

## Updating

The configured unsigned Squirrel feed is recorded in `build/manifest.json` and copied to the payload as `update-config.json`. `Update.exe` is bundled from the pinned Squirrel package. The application must handle Squirrel lifecycle arguments before creating its GUI and present update checking, progress, restart consent, and errors. Packaging alone is not evidence that these application workflows work. Do not embed release credentials in the executable or feed.

## Limits

A fresh-host bootstrap, actual installer lifecycle, offline startup, and running updater have not been proven by these source files. A failed bootstrap reports the precise download or verification failure and returns a nonzero exit code. No installer fallback format is used. This category has no HTTP API, so Postman collections do not apply.

