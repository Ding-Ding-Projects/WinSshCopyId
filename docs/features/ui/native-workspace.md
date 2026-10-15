# Native SSH workspace

The application uses a WinUI 3 workspace with Connections, Keys, Results, Tools, History, Settings and Help destinations. A custom title region preserves native caption buttons and a dedicated draggable region. The left navigation collapses below 1000 DIP into a labelled searchable drawer. Connections moves its record selection into a drawer at compact widths. The minimum requested window size is 640 by 480 DIP; actual scaled layout remains subject to runtime verification.

## Connections and credentials

Saved connection records contain labels, hosts, username, port and an opaque public-key reference. Passwords are never part of these records. Portable import validates connection metadata and clears the live password and selected key. An embedded key path is retained for format compatibility but is never resolved, probed, read or used to locate a private sibling automatically, including on restart and saved-record selection. A localized notice requires explicit public-key selection through the picker before deployment. Only that deliberate selection performs key-file validation. Portable export omits credentials. Legacy encrypted credentials migrate through the existing verified Credential Manager procedure during normal-profile initialization.

Installation reviews exact targets and the selected public-key fingerprint before starting. New SSH host identities require a decision on the UI thread while the worker waits with cancellation and a two-minute upper bound. Closing the application cancels active operations and clears the live password field. An operation password is held only in operation scope; it is not put in history, narration or serialized view state.

The cancellation control requests cancellation. It does not immediately declare a cancelled result or roll back changes already made on the host. Results remain per target. Automatic replay is unavailable unless the service can prove that retry is safe for that target. Mixed verification/deployment retry holds one cancellation lifetime through both phases and the intervening review. Cancellation after verification prevents another review, and cancellation during review overrides a late Continue answer. Deployment still requires explicit Continue; it never restarts with a fresh cancellation state.

## Search and presentation

Each registered search control owns an independent query and flags. Its adjacent native flyout evaluates literal or regular-expression searches through the actual bounded .NET engine and provides samples, captures, replacement preview, timing and guided insertions. Structured explanation, persistent snippets, suite import/export and match navigation remain incomplete and are labelled accordingly.

The native resource dictionary provides semantic light, dark and high-contrast brushes and button state layers. It is a foundation, not proof that every component meets the complete Material Design 3 contract. Settings currently persist theme, language choice, density, display name, funny levels, reduced-motion preference and accessibility-control visibility. Ready-state wording responds to all five funny levels. Complete localized copy and all-message-category funny-level coverage remain unfinished.

## Deterministic owned evidence data

Run the executable with `--evidence-profile <absolute-directory>` to route workspace settings, sessions, saved connections and host-trust paths into that explicitly selected directory. It must differ from the normal application-data root. This is an explicit application data provider, not an environment-variable override or an operating-system isolation claim.

The evidence profile visibly identifies itself. It does not scan the user's SSH directory, migrate or read remembered credentials, run SSH installation, or generate keys. Its empty records are real empty data. Any deliberately supplied fixture profile must be labelled fixture evidence, and no fixture result proves an SSH operation. Normal profile state is not changed by selecting an evidence directory.

Squirrel maintenance arguments `--squirrel-install`, `--squirrel-updated`, `--squirrel-uninstall` and `--squirrel-obsolete` exit before a window or profile is created. No explicit Squirrel-aware metadata is added by the UI layer; packaging owns shortcut behavior.

## Remaining acceptance work

The first native verification found a settings-save page-loss defect and bilingual title-bar clipping. The repair keeps the existing page/control instances alive and updates their localized presentation in place after a successful settings write. Event subscriptions are installed once. Adaptive layout avoids missing-page lookup during construction. Minimum sizing uses native minimum-track handling instead of recursive resize calls, and initial window dimensions use the current window DPI. The title row measures its bilingual content in logical units. These changes require the identical native sequence and viewport rerun before either finding is considered verified fixed.

Collection filtering now snapshots display data on the UI thread, debounces updates, evaluates in the background under per-expression and total-work limits, and discards superseded results. Empty and no-match states are explicit. Profile import intentionally leaves its embedded key reference unresolved and requires explicit selection. Export replacement requires two independent confirmations and a full-range slider; public-key and results output is staged before atomic promotion.

The focused `tests/WinSshCopyId.UiLogic.Tests` runner links the actual pure UI boundary types and exercises 11 behavioral checks, including opaque path metadata, explicit selection and cancellation at retry phase boundaries. It requires .NET SDK 8.0.424 through its test-only pin. It does not launch the native UI, contact a network share, or establish native runtime acceptance.

The first source unit is not complete product delivery. Persistent full revision browsing, complete tab management, full per-element appearance and locks, advanced accessibility/narration/schedules, complete command-palette teleport, full localization, universal search filtering, and native converter/model/server management remain unfinished. Runtime UI interaction, accessibility, genuine screenshots, high-DPI layout, native title-bar behavior, installation and update evidence have not been established by source inspection.

The original vector mark is in `design/assets/product-mark.svg`. PNG and ICO derivatives retain its geometry. ICO frames are 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels. Packaging must include them and verify the icon in the installed application; frame inventory alone does not prove that result.
