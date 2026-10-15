# Native SSH workspace handoff

## Intent and scope

WinSshCopyId opens as a working connection workspace. The first useful action is to choose or enter an SSH destination, select a public key, inspect the destination identity, and install the key. No hero, feature-card catalogue, download button, invented connection, or simulated success belongs on the production start screen. Reference records use reserved example hostnames and are labelled sample data. Production empty state has no seeded records.

The target is native WinUI 3 with project-owned Material Design 3 Expressive component adapters. Keep the operating system's accessibility, text input, high contrast, DPI and window-management behavior. A custom title bar must use real native minimize, maximize/restore, close, drag, double-click, snap and system-menu behavior. Reference window buttons disclose reference-only behavior and are not proof of native chrome.

Baseline: `d9b3ce8d2e64fc2f1c978f4a97d4aaecacb8457f`. This handoff is an initial complete architecture, not a declaration that all required features are implemented. An independent review must accept it before implementation. Every requirement below needs its own implementation, source-bound runtime evidence, documentation, localization and negative regression inventory.

## First implementation slice

1. Build `WorkspaceShell`, `WorkspaceTabs`, `SearchScope`, `TargetContext`, `MaterialButton`, `MaterialTextField`, `MaterialListRow`, `MaterialSurface`, `MaterialStatus`, and `MaterialAnchoredPanel` in the real framework. All consumers share the same native action registry, localization resources and target identity.
2. Implement Connections first: empty collection, editable host/user/port/name, native key-file browse, validation, selection, install confirmation, real progress/cancel, per-target success/partial/failure and retry. Use existing tested SSH services; no shell-command textbox or mock result can substitute.
3. Wire the custom title bar, left default tab strip, local list search with adjacent full regex entry, keyboard order, selected-target detail, Results navigation, notifications/history, provenance display, and compact layout. Shared search must not be delivered with a regex-looking button that does nothing.
4. Keep unfinished destinations visible as explicit implementation status in development builds, never as completed features or released empty shells. Continue through the remaining inventory before claiming full-scope delivery.

First-slice exit evidence: real native build, exact source and executable hashes, native window controls, keyboard-only connection operation, empty/error/cancel/partial results, isolation of two search scopes, focus return from overlays, all languages/themes and required DPI tuples. This slice is not final product acceptance.

## Geometry and hierarchy

Normal client area is 1200 × 800 DIP; minimum is 640 × 480 DIP. Physical pixels are recorded separately at 100%, 125%, 150%, 200%. Never apply an additional application zoom to compensate for DPI.

At 1200 DIP: 40 DIP custom title bar, 224 DIP default left tab/navigation strip, 24 DIP workspace padding, 20 DIP content gap, 280 DIP records pane, flexible detail pane with 360 DIP minimum, and a 28 DIP status strip. Tabs and lists scroll independently. Heading is 24/32; action row is 40 DIP high; touch mode uses 48 DIP targets. First screen uses blue only for selection, primary action and focus.

At widths below 1000 DIP: collapse the left strip to a labelled edge affordance, not rotated labels; retain full accessible names and a searchable tab drawer. At widths below 820 DIP: Connections becomes list → detail with a visible Back to connections control, preserving selected record and list scroll. At 640 DIP: forms use one column, action rows wrap, detail sections remain real tabs and outer content scrolls. No fixed-height content card may hide validation or secondary language. Side panels become bounded sheets only where anchoring cannot fit, retain the exact target name and return focus.

Show bilingual labels on two lines: English primary, Cantonese secondary at body-small with full contrast. Never force both into a single abbreviated line. Native labels wrap; values, fingerprints and commands offer horizontal scroll or explicit copy. Error text wraps without moving the primary action outside reach. High contrast removes decorative fills but retains borders, state names and focus.

## Semantic design tokens

| Role | Light | Dark | Usage |
| --- | --- | --- | --- |
| primary / on-primary | `#245FA6` / `#FFFFFF` | `#A9C8FF` / `#07305C` | Primary action, active indicator |
| primary-container / on-container | `#D9E7FF` / `#12345F` | `#28476B` / `#D9E7FF` | Selected record and tabs |
| surface / on-surface | `#F8F9FD` / `#1A1C20` | `#111318` / `#E2E2E9` | Window and primary text |
| surface-container | `#EDF0F7` | `#1D2027` | Navigation and secondary regions |
| surface-container-lowest | `#FFFFFF` | `#0D1015` | Input and raised panel |
| outline / outline-variant | `#717783` / `#C4CAD5` | `#8B919D` / `#414753` | Input boundary / separators |
| error / on-error-container | `#BA1A1A` / `#410002` | `#FFB4AB` / `#FFDAD6` | Failure facts, never colour alone |

These are seed-derived reference values, not a license to skip measured contrast for overrides. Selection uses a filled container plus leading indicator. Focus uses a 2 DIP high-contrast ring with 2 DIP offset. Shape: text field 12, buttons 20, records 12, surfaces 20, overlays 24 DIP. Spacing scale: 4, 8, 12, 16, 20, 24, 32 DIP. Typography: platform UI face with installed CJK fallback; title 24/32 semibold, section 18/26 semibold, body 14/20 regular, label 14/20 semibold, supporting 12/18. Never depend on a remote font.

Motion: state layer 100 ms, panel entry 200 ms, navigation 250 ms using emphasized deceleration; exit 150 ms. No perpetual decorative animation. Reduced motion uses instant state changes and a textual operation state. Authored motion remains stored separately from reduced-motion rendering. Dynamic rainbow is a typed sentinel outside colour swatches, driven by one shared speed-level consumer; it settles to one hue under reduced motion.

## Registered component anatomy and provenance

The reference follows Material Design 3 component anatomy. Production must register actual native adapters and preserve their template/version provenance in a hand-written inventory. Styling a stock control blue is insufficient evidence. Reference authorities: Material buttons, text fields, lists, navigation, dialogs, menus, progress indicators and colour roles at `https://m3.material.io/`; Windows App SDK native accessibility and custom title-bar APIs at `https://learn.microsoft.com/windows/apps/`. Exact source/template revision and acceptance remain pending until implementation.

| Adapter id | Required anatomy | Native consumer |
| --- | --- | --- |
| `md.surface` | Shape, surface role, outline/elevation, explicit content padding | Border/Grid template; no transparent overlay |
| `md.button.filled` / `outlined` / `text` | State layer, content, icon slot, focus, disabled and busy treatment | Button with shared command and automation name |
| `md.textfield.outlined` | Persistent label, outline, value, trailing action, supporting/error text | TextBox/PasswordBox plus semantic wrapper |
| `md.list` / `md.listitem` | Leading selection/icon, headline, supporting line, trailing action | Virtualized ListView with selection model |
| `md.navigation` / `md.tabs` | Active indicator, label/icon, focus, overflow, axis-aware keyboard | Shared native tab workspace, not independent menu buttons |
| `md.menu` / `md.panel` | Opaque bounded surface, local search, item shortcuts, scroll, anchor | Anchored native popup with focus restoration |
| `md.dialog` | Title, exact target facts, body, decision actions | Native content dialog only for required decisions |
| `md.progress` / `md.snackbar` | Determinate value when available, textual state, cancel/retry | ProgressBar and nonblocking notification centre |
| `md.switch` / `md.slider` / `md.checkbox` | Track/thumb or indicator, label, value, keyboard path | Native controls calling the same setting/action service |

Reference markup declares these adapter identities for review only. Production inventory must reject an unregistered replacement, including a generic text field standing in for a typed configuration control.

## Shared target context and discovery

Every rendered element, state and pseudo-state receives a stable `TargetContext`: surface id, element id, record id if present, state, action id, property path, anchor, lock id and return-focus token. IDs never depend on localized text, list position or a mutable display name. Virtualized rows retain record identity. Context-click, Shift+F10, menu key and touch long-press all resolve the same exact target. Shift+context-click opens appearance directly where supported.

Each context menu includes applicable commands, `Edit appearance…`, `Lock this element…`, actual keyboard shortcuts, its own search and adjacent anchored full regex builder. The menu's own elements have target contexts too. Avoid eager infinite nesting: create child editors only on explicit invocation and retain bounded parent context. A locked native control uses an operable wrapper; all entry routes consult the same policy before command execution. A disabled state never silently prevents the user from reaching its unlock prompt.

Every collection, editor section, settings tab, popup, dropdown, picker, context menu, notification and history surface owns `SearchScope(surfaceId, instanceId)`. Own query, plain/regex mode, pattern, flags, validation, query history and snippets independently. Plain text is default. Search and its builder synchronize bidirectionally. Scoped no-match text states the target and offers Clear. Count announcements are polite and debounced. Every search has a visible adjacent builder button; constrained layout opens an attached sheet with return focus, never another tab.

The shared full regex workspace includes guided/raw construction, literals/Unicode/classes/anchors/groups/alternation/quantifiers/lookarounds/backreferences/modifiers/replacement; actual engine/version/dialect and capability matrix; unsupported constructs with exact reasons; parse tree and token annotations; live match/capture tables, replacement preview, tests, snippets/import/export, match navigation and zero-width handling; timing, risk diagnostics and bounded trace where supported. Run evaluations in cancellable bounded isolation, not the UI thread. Each internal collection has independent search; nested builder construction is lazy. A visual preview of this workspace is not proof of engine behavior.

## Destinations and states

| Destination and stable id | Working layout and actions | Empty / failure / partial states |
| --- | --- | --- |
| Connections `connections` | Saved records + selected target tabs: Connection, Key, Review. Add/edit/duplicate/import; host/user/port; native browse public key; inspect identity; install selected public key; selection and batch preview | No saved connections → Add connection; invalid host/key inline; unknown host requires real identity decision; unavailable client names recovery; operation reports cancelled/partial/failed per host |
| Keys `keys` | Public-key list + metadata/fingerprint/detail; generate/import/export/open; algorithm selector from supported service, key path with browse | No key → Generate or Import; unreadable/unsupported key retained with reason; never display private-key content or put secret in captures |
| Results `results` | Operations list and selected phase/result; target, action, start/end, exact state, retry/open details/export | No operation → route to Connections; running retains cancel; partial count explicit; retries select failed items without replaying successes |
| Tools `tools` | Compact labelled tool tabs opening real workspaces: Regex, Authenticator, Converter, Local models, Minecraft servers | Never a generic command catalogue; missing runtime keeps useful detection/help/recovery actions |
| History `history` | Local revisions and notifications as tabs; date range, real action facets, searchable diff, restore preview, label/export, selection | No revisions stated; history-write failure does not reverse the user action; restore appends, never rewrites |
| Settings `settings` | Full workspace tabs: General, Appearance, Language, Accessibility, Narration, Schedules, Locks, Integrations, Advanced | Every setting has expandable explanation and provenance; failed persistence retains prior value and exact reason |
| Help `help` | Offline articles, contextual links, changelog, local support tickets, About | Missing article/version stated, never invented; version and build update time read from build metadata |

Each destination includes local search, full tabs, applicable bulk actions, export, contextual help, appearance/lock access and palette indexing. Global palette shortcut is `Ctrl+Shift+F`; results use live controls and focus the exact target, respecting locks. Filters/statistics collapse by default without losing their state. Accessibility tools remain one action away on every surface, with a discoverable Settings restoration route after deliberate hiding.

Tabs default left and dock left/right/top/bottom; reorder, pin, group, group decoration, collapse, overflow, keyboard movement and persistence apply equally to nested settings/detail tabs. Four separate searches cover current strip, each group, groups, and all owned windows/workspaces. Move into group uses an anchored searchable picker, never an unbounded menu. Both text-based bulk-close predicates preview exact matches and exclude pinned/locked/unsaved work by default. Empty/invalid query cannot close tabs.

## SSH review, trust and terminal-state contract

These are distinct addressable design states, not synonyms for a generic warning. Each uses the selected connection's stable record identity, entered hostname and port, resolved endpoint when known, SSH host-key algorithm and complete fingerprint. Fingerprint values come from the real service and are copyable; the reference never fabricates one. Review shows the public-key fingerprint separately, explicitly labelled, so it cannot be confused with the server identity.

| State id | Visible facts and permitted action | Transition and persistence |
| --- | --- | --- |
| `review` | Exact account, host, port, selected public-key identity and destination operation; Edit and Cancel remain available | Explicit Install begins inspection; merely opening review changes neither trust nor the remote host |
| `unknown-host` | Host + port, algorithm, full observed fingerprint, no prior approval; ask the user to compare it with an independent trusted source | Explicit Approve this identity persists the exact endpoint/key binding through the native SSH trust store before continuing; Cancel writes no approval |
| `changed-host` | Exact endpoint plus previous and observed algorithm/fingerprint; explain that identity differs | Hard stop: disable installation and do not replace the saved approval. Offer Cancel, copy fingerprints and documented investigation. No ordinary Retry, accept-any or bypass control |
| `working` | Actual current phase, completed targets, active target, elapsed time and progress when measurable | Submit rejects re-entry; Cancel requests cancellation through the operation owner |
| `cancelling` | Cancellation requested, active phase/target and completed changes retained | Keep this state until the service reports a terminal outcome; repeated Cancel is idempotent. Never show Cancelled from a button click alone |
| `cancelled` | Exact completed, skipped and not-started targets, plus uncertain in-flight target if confirmation is unavailable | Completed SSH changes remain installed. Offer inspect/reconcile for uncertain targets; never silently roll back or claim that all effects were undone |
| `partial` | Per-target terminal outcomes and exact counts, including completed work | Retry selects only eligible failed targets after fresh identity validation; completed targets are excluded and retained |
| `success` | Confirmed service outcome for every selected target, installed/already-present distinction and completion time | Readable result and export; success is unavailable while any target is pending, uncertain, failed or cancelled |

Changing host/port or observed identity invalidates a pending approval. An approval for one endpoint never authorizes another. Review/cancel paths return focus and preserve the user's inputs. Loss of the SSH connection after mutation is an unknown outcome requiring reconciliation, not automatic failure with safe replay. Persist trust only after explicit approval, and read it back before continuing; a trust-store write failure stops the operation with its exact reason.

## Keys workflow detail

| State/action id | Controls and facts | Validation and outcome |
| --- | --- | --- |
| `keys.empty` | Generate and Import are equally visible; explain public versus private material | No invented key record |
| `keys.generate` | Searchable algorithm picker populated from installed SSH service capabilities; algorithm-specific size/curve choices; comment; output folder with native Browse; filename; masked passphrase and confirmation where supported | Recommend Ed25519 only when available; show unavailable algorithms with the service reason. Never offer unsupported sizes/curves or silently change an explicit choice |
| `keys.output-review` | Exact public and private output paths, destination permissions, existing-file status and selected algorithm | Validate writable destination and filename, protect private material, reject equal/colliding paths, and preserve existing files on all failures. No private value enters history, logs or captures |
| `keys.overwrite` | Name every existing file that would be replaced; offer Choose another name and Cancel | Never overwrite automatically. Require the native two-key/full-slider destructive confirmation and a recovery-preserving transaction before replacement |
| `keys.generating` | Real phase/progress, cancellation and no re-entry | Report cancelled only after terminal settlement; atomically promote a validated pair or retain prior files unchanged |
| `keys.import` | Native public-key file browse and optional paste of public-key text; parsed algorithm, public fingerprint and comment | Validate bytes/encoding/key structure and supported algorithm; reject private-key input, malformed/truncated content and oversize payload. No extension-only trust |
| `keys.ready` | Public fingerprint, algorithm, source, created/imported provenance and selectable destinations; Export public key, Open folder, Install | Display metadata only; private-key material is never shown. A missing/moved source is explicit, not a valid cached path claim |
| `keys.invalid` | Preserve safe input and exact validation reason; Choose another file or edit | No partial record, no success toast and no overwrite; retry repeats validation |

Production must define service-backed bounds and supported algorithms from actual capability detection. The handoff intentionally does not invent an algorithm compatibility claim. Bulk actions preview affected public-key records and exclusions; deletion names whether it removes a record, a public file or a private file, and follows the applicable destructive confirmation.

## Results workflow detail

| State/action id | Controls and facts | Required behavior |
| --- | --- | --- |
| `results.empty` | No operations yet; Open Connections | No sample success in production |
| `results.running` | Operation id, account/host/port, public-key fingerprint, real phases, target count, elapsed time and active target | Phases distinguish validation, host identity, authentication, inspection, write and verification; unknown timing/progress stays unknown |
| `results.cancel` | Cancel beside the initiating operation and same action in its detail | Shared operation owner changes to Cancelling, rejects duplicate submits and waits for terminal settlement |
| `results.terminal` | Per-target installed, already present, failed, cancelled, skipped or uncertain outcome, exact reason and completion time when known | Aggregate never hides mixed outcomes; completed SSH mutations remain retained after cancellation or partial failure |
| `results.retry-failed` | Reviewable eligible-failure list, selected count and explicit exclusions | Retry only selected failed targets after fresh validation/trust checks. Do not replay completed targets, changed-host hard stops or uncertain mutations without reconciliation |
| `results.export` | Current selection versus filtered view, format, encoding, output path with Browse, omitted-sensitive-data disclosure | Export the chosen scope faithfully; exclude credentials/private key data; validate output and offer Open in VS Code with an honest missing-editor state |
| `results.details` | Searchable sanitized phase records, target-level detail, copy and export | Preserve exact service facts, do not expose secret/environment content, and do not infer success from a process starting |

## Tool workspaces

**Authenticator:** issuer/account collection; explicit add via URI, image/clipboard QR, camera if available, manual parameters; pairing preview behind explicit reveal; confirm current code before enabling. Current/next code with numeric countdown, copy, reorder/groups and bulk actions; clock/credential-store unavailable state. Secrets never appear in reference data, screenshots, ordinary exports or history. Password-protected history uses encrypted or redacted snapshots with stable identity.

**Converter:** source queue, category tabs (Documents/PDF, Images, Audio, Video, Archives, Structured Data/Spreadsheets, Code/Text, Binary Encodings), adapter list, preview and output options. Every category has its own search. Enabled adapters require bundled offline proof, unavailable formats stay visible with reason. Expose PDF inspect/split/merge/extract/reorder/rotate/metadata. Persistent paged queue, bounded concurrency, storage preflight, pause/resume/cancel and per-file outcomes. Atomic output validation, loss disclosure and overwrite confirmation precede success.

**Local models:** Runtime, Model Store, Queue, Installed, Chat, Launch profiles and Diagnostics tabs. One-click managed installation is separate from explicit runtime start. Exhaustive verified catalogue plus installed tags, source age/completeness, conservative hardware-fit evidence, exact variants and batch pull preview. Streamed local chat has session selection, parameters, capability-bound attachments, cancellation and redacted export. No invented models or fake installed state. Offline with no verified catalogue is explicitly unavailable.

**Minecraft servers:** separate working destination under Tools. Inventory list plus selected server with Overview, Configuration, Worlds, Players, Extensions, Network, Schedules, Logs and Backups tabs. Create/adopt wizard: target → edition/flavour → complete version catalogue → runtime/storage/network → review → install. Four target adapters: local Windows process, local Docker, SSH process, SSH Docker. Discovery is read-only until explicit adoption. Disconnected is distinct from stopped.

Version catalogue has channel filters, pagination completeness, retrieved-at/stale state, exact unavailable-distribution reasons and immutable source IDs. Rich capability descriptors drive switches, selectors, numeric controls, permission matrices, nested collections and typed command builders, never raw property-only forms. Lifecycle, installation, plugin/mod/pack provenance/compatibility, worlds, backup/restore, upgrades/rollback and topology remain real workflows. Consequential changes preview compatibility and backup; unknown compatibility is not safe compatibility. Acceptance records edition × version × flavour × target × extension separately. Sample testing never proves all-version support.

## Settings and advanced surfaces

Appearance includes live theme/density/seed/font, display-name-only rename, local logo presets/upload with safe conversion, complete per-element editor and motion workspace. Element editor tabs: Layers, Selection, Typography, Colour, Geometry, Effects, States, Animation and Transition, Preview, History. Preserve unsupported renderer values visibly with reasons. Include non-destructive layers/groups/masks/channels/adjustments/embedded content, gradients/images/strokes/shadows, transforms/warp/perspective/crop, full typography and numeric alternatives. Motion includes tracks/keyframes/easing/timing/loops/triggers/composition/timeline/playback and reduced-motion view. Reset/undo/presets/import/export operate at property/layer/state/element/global scopes. Editors can target themselves using stable contexts.

Colour uses continuous field plus HEX/HEX8, RGB/A, HSL/A, HSV, HWB, Lab/LCH, OKLab/OKLCH, CMYK and named representation, alpha/gamut/contrast diagnostics and shared rainbow option. Font picker enumerates installed/bundled faces with previews and CJK fallback. Every picker has isolated search and builder.

Language has English, Cantonese and bilingual, independent 1–5 funny levels defaulting to 5, truthful first-run disclosure, dialog emoji switch and visible local personal-vocabulary JSON upload with empty/valid/invalid/replace/clear states. No private mapping is bundled. Shared School mode is renameable, live across running applications, locally unlocked, explicitly a convenience lock; when enabled, prohibited capabilities are omitted from every route/search rather than disabled. Preserve previous values for restoration.

Accessibility exposes focus, contrast, scale, keyboard/help/read-aloud and five independent off-by-default accommodations. Narration is off by default, has per-language real voice pickers, stable identity and effective-voice status, rate/pitch, late enumeration, offline/no-voice states and serialization. Screen reader has precedence. Schedules use accessible date/time controls, timezone/DST semantics, weekdays, precedence and local/API/Home Assistant sources; external credentials stay in the platform vault and stale values never overwrite base settings.

Locks use exactly six independent policies: PIN; password; PIN+password; password+TOTP; PIN+TOTP; password+PIN+TOTP. Each target owns its credentials and duration. PIN keypad and manual input share validation/rate limits. Support Tickets is local fiction with an unstyled no-network/no-person disclosure and an Open data folder action, never silent deletion. Unlock ladder clears waiting only, retains credential and attempt limits, and follows the required challenge budget. School mode starts at arithmetic.

Advanced includes real service timeout/concurrency/proxy/path controls with native browse, beginner presets mapped to actual values and explicit Custom state; no unvalidated generic shell form. Every setting displays actual compiled default or stored-source provenance. All collections retain scoped selection, reviewable bulk operations, honest partial results and applicable faithful export formats. VS Code opening and external-editor choice report missing editor explicitly. Destructive operations use native anchored two-key/full-slider confirmation with always-available cancellation.

## Verification and remaining evidence

Reference files do not prove native operation, registration provenance, storage, accessibility or rendering parity. Independently inspect each state at normal and minimum client area × three languages × light/dark × 100/125/150/200% (48 tuples per state), including long bilingual labels and expanded inline validation. Add high contrast, keyboard, reduced-motion and large text cases. Record source SHA, artifact SHA-256, state, viewport DIP/physical pixels, actual display scale, method and genuine capture; preserve historical references with original provenance.

Use a hand-written inventory of required controls, search scopes, target contexts, settings explanations, capabilities and native consumers. Remove each required registration/consumer in turn to prove the relevant negative regression fails. Never infer a missing item from scanning only what happens to exist. A screenshot cannot prove a hidden keyboard route, a still cannot prove motion, and a saved preference cannot prove its live renderer consumer.

Open delivery items: independent review; native implementation of all destinations/shared components; exact native Material adapter provenance; comprehensive localization/copy levels; complete regex engine workbench; full appearance/motion capability matrix; all server versions/targets/extensions; runtime capture matrix; packaged/install behavior. No shipped-completeness claim is made by this handoff.
