# Documentation privacy and preferences

## Behavior

This website is a reading and delivery-information surface. Its controls operate on documentation and the current visitor's browser state. They do not control the installed application, SSH endpoints or servers.

## Configuration

Visitor settings are stored for this origin in local browser storage. Clearing that storage resets preferences. Private browsing, storage denial or quota exhaustion can prevent persistence and must produce a visible notice. Language settings offer English, Cantonese and bilingual presentation. Independent humour levels style messages without changing technical facts.

## Local wording import

A user-selected JSON file may contain exactly `schemaVersion: 1` and `entries`, an object mapping original strings to replacements. The loader permits at most 256 KiB UTF-8, depth 8, 160 Unicode code points per nonempty key and 1,000 per value. It rejects duplicate keys, unsafe object keys, unsupported versions, unknown properties and non-string values. Invalid imports never partially apply. A valid prior cache remains until replacement or explicit clearing. Clearing restores original labels immediately.

## Privacy and exports

No analytics, CDN scripts or remote fonts are required. Local wording data and source-file metadata are excluded from history, exported settings, diagnostics and reports. Imported replacements apply only to private interface labels, never source articles, technical commands, paths, addresses or external records.

## Failure and verification

Browser-only state is not an operating-system credential vault or cross-application synchronization channel. Advanced universal controls without implemented browser equivalents remain explicitly incomplete in the feature ledger. Source checks alone do not establish keyboard, screen-reader, touch, localization or visual acceptance.

## Related articles

- [Delivery evidence](delivery-evidence.md)
- [First session](../getting-started/first-session.md)
