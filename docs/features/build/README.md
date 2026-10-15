# Build and delivery

Run `build.bat --run` on 64-bit Windows to acquire pinned project-local tools, publish the real application, verify the payload, and launch it. `build.bat /s` is noninteractive and does not launch. `download-dependencies.bat /s` only acquires tools. `build-installer.bat /s` builds and creates the genuine unsigned Squirrel.Windows installer without installing or publishing it.

The portable bootstrap needs no administrator rights and does not change system PATH, security settings, or existing SDK installations. HTTPS downloads are pinned in `build/manifest.json` and verified before extraction. Extracted tool files are inventoried and checked before reuse. The bootstrap uses an exclusive lock, a durable activation journal, archive-derived file hashes, exact file inventory, path containment checks, and a preserved previous extraction. Interrupted or malformed journal recovery retains evidence and requires a verified active or backup tree. A missing or changed tool is reacquired from the pinned archive.

## Outputs and source binding

`artifacts/publish/WinSshCopyId.exe` is the runnable native WinUI 3 x64 executable. Both the .NET runtime and Windows App SDK runtime are bundled. `artifacts/releases/` contains `Setup.exe`, `RELEASES`, and the versioned full NuGet package. The installer is unsigned and can show an unknown-publisher warning. Build and installer receipts bind file hashes, manifest hash, version, runtime, and exact committed source. Builds reject staged, unstaged, and untracked source changes. The installer reuses an unchanged-source build only after validating the entire receipt, exact file inventory, every payload hash, manifest, version, runtime, source tree and index tree. If any proof is absent or stale, it invokes the exact root `build.bat /s` and validates the resulting receipt before packaging.

`BUILD_VERSION` optionally sets a numeric `major.minor.patch` version. The hosted job uses `1.0.<run number>`. Builds and packaging do not publish or install. Local runtime acceptance, installation, upgrade, cancellation, uninstall, rollback, and fresh-machine behavior require separate evidence.

## Hosted delivery

The only job uses the standard `windows-2022` hosted image. Every branch push and manual dispatch builds and packages through the root entrypoints. There are no test, lint, type-check, security scan, self-hosted, or paid-runner jobs. The job inventory is: portable .NET SDK 8.0.426, NuGet 6.14.0, Squirrel.Windows 2.0.1, project package references restored by the SDK, built-in Windows PowerShell and .NET Framework for packaging, and the hosted GitHub CLI for publication. Bootstrap is part of both root build paths and requires no warm SDK cache.

Every branch publishes a normal release after proving its candidate descends from main lineage. Only the exact main head is marked latest; an older main run still publishes normally without changing latest; task-branch releases use `--latest=false`. Unique immutable tags include the version, run ID, and attempt. The publisher validates receipts and asset hashes, creates a normal release, resolves the exact tag source, reads back draft/prerelease state and asset sizes, then downloads and hashes every published asset. Other branches publish their normal release assets without changing the public update feed. Safe output collection runs even on failure. Successful uploads contain only receipts; failed or cancelled runs preserve safe installer outputs and receipts with one-day retention. Source and dependency caches are never uploaded.

## Updating

The configured unsigned Squirrel feed is recorded in `build/manifest.json` and copied to the payload as `update-config.json`. `Update.exe` is bundled from the pinned Squirrel package. The application must handle Squirrel lifecycle arguments before creating its GUI and present update checking, progress, restart consent, and errors. Packaging alone is not evidence that these application workflows work. Do not embed release credentials in the executable or feed.

## Limits

A fresh-host bootstrap, actual installer lifecycle, offline startup, and running updater have not been proven by these source files. A failed bootstrap reports the precise download or verification failure and returns a nonzero exit code. No installer fallback format is used. This category has no HTTP API, so Postman collections do not apply.



## Bundled worker and local history runtime

The build publishes `src/WinSshCopyId.ToolWorker` as a complete self-contained x64 payload under `Tools/Worker`. `Tools/worker-manifest.json`, outside that directory, binds the exact producer source and every worker member's slash-normalized relative path, byte length and SHA-256. This inventory is packaging proof only. Conversion remains unavailable until the native confined execution verifier supplies the adapter-specific runtime receipt.

The pinned official MinGit x64 archive is verified against its published Git for Windows asset digest, fully extracted and inventoried, then bundled under `Tools/MinGit`. Its original `LICENSE.txt` and component licenses remain included. The exact corresponding source URL and archive digest are in `Tools/mingit-manifest.json`, with full member hashes, version, runtime and executable lookup `cmd/git.exe`. It is application-local and does not replace installed Git or change persistent PATH. Workspace history integration must resolve that exact packaged executable and preserve the existing encrypted-history boundary.

`build-info.json` in the published root records the actual source commit, version, build-start UTC timestamp (`builtAtUtc`, ISO 8601), runtime and manifest hash. It is included in the payload hash receipt. The native interface may localize this recorded timestamp, including seconds, but must not replace it with launch time or file modification time.

## Website route

`build.bat --site /s` invokes the documentation-owned `scripts/build-site.mjs` through a verified Node.js 22 or newer. If a compatible installed executable is unavailable, it acquires the exact official portable Node.js version and archive digest from `build/manifest.json`, verifies and inventories the extraction, and invokes that executable directly without global installation or persistent PATH changes. An absent documentation helper is an honest nonzero result. The helper owns website output validation; the root route records successful source-bound invocation in `artifacts/site-build-receipt.json`.

Hosted output retention is one day. Successful publication uploads only receipts because release assets are durable; failed or cancelled runs preserve only explicitly safe installer outputs and receipts when present. Dependency caches and source trees are never uploaded.


## Installer and persistent data separation

The inaugural Squirrel package ID is `WinSshCopyId.Desktop`, declared once in `build/manifest.json` and passed into the NuGet specification. Stock Squirrel therefore installs under `%LocalAppData%\WinSshCopyId.Desktop`. The product executable and AppMarker remain `WinSshCopyId`; persistent settings and encrypted data remain under `%LocalAppData%\WinSshCopyId`. These are sibling directories, with neither containing the other. Packaging never migrates, deletes, or initializes the data directory.

The package ID drives input and full-package filenames, installer receipts, release validation and `update-config.json`. The update feed URL stays unchanged. This identity was selected before the first release or installed update identity existed. An established installed identity must not be renamed through this procedure. Installation and preservation behavior still require actual current-host acceptance; source path separation alone is not that proof.
