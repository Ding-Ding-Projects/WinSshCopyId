# Native evidence receipts

`scripts/verify-native-evidence.mjs` is the explicit Windows App SDK / UI Automation adapter for inspected native-window PNG evidence. It does not use browser console counts, DOM measurements, or Flutter hooks. `scripts/stage-native-evidence.mjs` stages unchanged PNG bytes and retains a rollback transaction.

## Evidence boundary

The validator establishes consistency between retained files, source identity, build identity, native observations, inventory rows and documentation. It cannot establish that an operator inspected pixels correctly or that a producer honestly recorded its observations. Original launch, window-enumeration, UI Automation, capture and teardown records must remain beside the derived sidecars. Synthetic tests exercise rejection only and are never product captures.

The supported route is `direct-cheap-cli-with-uia-helper`: the installed cheap Lowlevel CLI launches the application and a task-owned UI Automation helper on the same named hidden desktop. Native InvokePattern, SelectionItemPattern and ScrollItemPattern actions are labelled as such. HWND-targeted capture preserves raw PNG bytes. A task-owned resize helper can use the installed Lowlevel desktop binding and `SetWindowPos` with `SWP_NOACTIVATE`; it records actual DPI and dimensions, without changing host display settings.

## Receipt fields

A private receipt uses `version: 1`, `kind: "winui-native-window"`, the route above, and an exact stable `id`. All SHA-256 values are lowercase. Referenced JSON files must be hashed and contained within the separately supplied run root. Private runtime paths and identities do not belong in published documentation.

- `source`: `startCommit`, `endCommit`, `startedAt`, `completedAt`, `cleanAtStart`, `cleanAtEnd`, `artifactPath`, `artifactSha256`, and `buildBinding: {path, sha256}`. Record source observations prospectively. The captured revision must be an ancestor of the current repository HEAD, and `src`, `build`, root build entrypoints and `global.json` must have no committed, staged, unstaged or untracked difference from it. Documentation-only promotion may follow the captured revision.
- The derived build-binding sidecar contains `kind: "derived-build-binding"`, `sourceCommit`, `artifactSha256`, `originalReceipt: {path, sha256}`, and `artifactEntry`. Preserve the original `build-receipt.json` bytes. `artifactEntry` is the exact matching object from its `files` array, including path, hash and byte count. The validator checks the original source, build time, entry, actual executable bytes and hashes.
- `capture`: `rawPath`, `promotedPath`, `sha256`, `rawSha256`, `mimeType: "image/png"`, `bytes`, `width`, `height`, `startedAt`, `capturedAt`. Record the capture interval around the real tool call. Never infer dates from filenames. The PNG structure, CRCs, dimensions, textual-metadata absence and byte equality are checked; visual decoding remains part of the separate inspection.
- `state`: `screen`, `state`, `theme`, `language`, `captureKind: "window"`, and `viewport: {width, height, scale}`. The viewport is the logical outer-window size, not the client content area. Its scaled dimensions must equal the original PNG. `scale` equals observed DPI divided by 96. Retain client geometry separately when making client-layout claims.
- `runtime`: `launchPid`, hexadecimal-string `hwnd`, `hwndResolvedLive: true`, `geometry: {dpi, windowWidth, windowHeight}`, `diagnosticKind: "winui-uia-and-application-events"`, `interactionProofId`, and hashed `identity`, `interaction`, `diagnostics`, `cleanup` references. Browser diagnostic count fields are rejected.
- Identity sidecar: `pid`, `hwnd`, `sourceCommit`, `artifactSha256`, `creationDate`, `executablePath`, `desktop`. Preserve the original complete process identity and live HWND ownership observations; PID alone is not authority to act.
- Interaction sidecar: `id`, `pid`, `hwnd`, `sourceCommit`, `captureSha256`, `observed: true`, and nonempty `steps`. Each step names `action`, `target`, `observedResult`, and `ok: true`. Include raw action/result receipt references and exact UI Automation identifiers. A returned invocation is not sufficient: record the independent semantic/pixel transition. Initial-state captures may use a launch-and-visible-state observation, without claiming interaction.
- Diagnostic sidecar: `kind` matching `diagnosticKind`, `pid`, `sourceCommit`, `querySucceeded: true`, `browserObservation: "not-applicable"`, `observedCrashCount: 0`, `providers` including `Application Error` and `.NET Runtime`, `startedAt`, `completedAt`. Record the actual bounded process-specific query, its successful result and retained event data. Its interval must cover the capture. This proves only observed application-event coverage, not complete native diagnostics or security.
- `inspection: {path, sha256}` points to `captureSha256`, `reviewer`, and explicit booleans: `decoded`, `pixelsInspected`, `expectedStateVisible`, `sensitiveDataReviewed`, `visibleDesktopUntouched`, `expectedSurfaceOnly` are true; `unrelatedTargetsObserved`, `mocked`, `handEdited` are false. Review both pixels and PNG metadata before asserting these facts.
- Cleanup sidecar: `pid`, `hwnd`, `desktop`, `ownedOnly`, `appAbsent`, `helpersAbsent`, `desktopAbsent`, `observedAt`. All four boolean assertions must be true. Retain actual normal-close and absence receipts. Missing observations never become success.
- `inventory: {path, recordId}` and nonempty `documentation: [{path, alt}]` identify the final checked-in records. Documentation image paths are resolved relative to each Markdown document. Commented image links do not count.

## Exact inventory linkage

The inventory is JSON with a `records` array. Exactly one row must match the receipt ID with `active: true`. Each row records these exact values:

`id`, `active`, `path`, `sourceCommit`, `artifactSha256`, `captureSha256`, `screen`, `state`, `theme`, `language`, `viewportWidth`, `viewportHeight`, `scale`, `interactionProofId`, `interactionReceiptSha256`, `inspectionStatus: "inspected"`.

Retire historical rows explicitly rather than relabelling their source. Add human-readable limitations and observation timestamps alongside the required fields. Root README and a relevant feature article should display representative current captures with accurate alt text. Do not publish raw private lifecycle receipts.

## Stage and validate

Create the exact destination directory first. Supply the existing target hash when replacing a previously promoted file. Keep the transaction and backup until all downstream checks and the second visual inspection pass.

```sh
node scripts/stage-native-evidence.mjs --mode stage --repo-root <repository> --run-root <owned-run> --raw captures/settings.png --target docs/evidence/settings.png --expected-raw-sha256 <sha256> --transaction stage-settings.json
node scripts/verify-native-evidence.mjs --receipt receipt-settings.json --repo-root <repository> --run-root <owned-run> --expected-commit <commit> --max-age-hours 168
node --test tests/evidence/native-evidence.test.mjs
```

On a downstream failure, restore only this transaction:

```sh
node scripts/stage-native-evidence.mjs --mode rollback --repo-root <repository> --run-root <owned-run> --transaction stage-settings.json
```

The focused tests demonstrate rejection of missing and changed images, changed source, changed product files, missing and modified sidecars, stale evidence, browser-field substitution, incorrect geometry, commented documentation, escaped paths, incomplete cleanup and mismatched build entries. Each rejection is paired with a valid fixture. Staging tests prove a wrong hash is rejected and an exact byte copy can be rolled back.

## Unavailable proof

An unsuccessful event-log query, missing runtime identity, stale source, unavailable build bytes or absent capture interval leaves promotion incomplete. Do not insert zero browser counts, invent a native observation, modify image bytes or use an older image for a newer source to obtain a passing result. Other scale/language combinations, SSH behavior, installer acceptance and release delivery require their own evidence.
