# Documentation workspace design

Status: proposed reference, not runtime evidence.

## Purpose and boundaries

WinSshCopyId is an installed native Windows application. This static documentation site explains its workflows and release evidence. It cannot connect to SSH hosts, install keys, run servers, or act as the installed product. Download buttons exist only when a validated release manifest supplies immutable asset URLs. No product screenshot is available at this design stage.

## Visual direction

Calm blue Material Design 3 reading workspace: primary #285EA7, on-primary #FFFFFF, surface #F8F9FF, container #EAF0FB, text #172034, outline #737C8C. Dark mode uses surface #10141C, container #202938, text #E0E6F2, primary #A8C7FA. Use 8px spacing, 12px card corners, 20px panels, 44px minimum targets, visible 3px keyboard focus, and reduced-motion support. System font stack is intentional and requires no font download.

## Addressable screens

- `#home`: product name, source version, exact source timestamp or unavailable state, explicit installed-product boundary, article entry points, factual verification summary.
- `#articles`: categorized complete articles and selection toolbar.
- `#wiki`: complete local wiki chapters, never an external-content substitute.
- `#status`: provenance and feature coverage ledger, distinguishing source implementation, tests, runtime evidence and release proof.
- `#settings`: per-visitor language, separate English/Cantonese humour levels, emoji, theme, accent, density, text size, local wording import and clear, history/export/reset.
- `#article/<id>`: full article, heading anchors, table of contents, related articles, source attribution.

## Composition and responsive behavior

A compact header holds the product mark, title and provenance. A horizontal navigation strip remains visible above the reading area. At 320px it scrolls internally rather than forcing body overflow. The desktop reading area uses an article index beside the content; below 760px the index collapses into an accessible disclosure. Article width is capped at 74ch. Tables and code scroll inside their own containers. Search controls and regex panel wrap into one column. Dialogs stay within viewport with internal scrolling.

## Interaction handoff

Search is scoped to the current content collection. An adjacent builder exposes literal/regex mode, case sensitivity, whole-word matching, pattern, flags, sample text, match results and syntax errors. The panel stays anchored to its search field. Ctrl+Shift+F opens a searchable command palette with focus restoration. Notifications are non-modal and retained in a review center. Visitor preferences and local history persist only in browser storage. Personal wording is validated, local-only, replaceable and clearable; its values never enter exported diagnostics or history. All dynamic text uses textContent or equivalent escaping.

## Reference tuples and acceptance

Screens above: 1440x900 and 320x740; English, Cantonese, bilingual; light/dark; scales 100%,125%,150%,200%. The source design is not a capture. Genuine runtime, keyboard/touch, contrast, overflow, component-provenance and design-parity evidence remain required. A feature ledger must name incomplete features without calling them shipped. Generic styled controls alone do not prove full Material Design 3 component conformance.

## Asset provenance

Use only the reviewed project vector mark or a typographic title. No fabricated screenshots, remote fonts, remote imagery, analytics or CDN runtime assets. The design document is public-bound source and contains no local machine paths.
