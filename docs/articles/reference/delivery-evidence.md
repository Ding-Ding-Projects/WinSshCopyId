# Understand delivery evidence

## Behavior

Delivery has separate stages: source implementation, local checks, native build, installer packaging, publication, installed runtime acceptance and live documentation verification. A successful earlier stage never silently stands for a later stage.

## Configuration

The native manifest is `build/manifest.json`. Root `build.bat` and `build-installer.bat` are the supported production entrypoints. The documentation generator takes version from that manifest and source identity/time from the committed revision. A source timestamp is labelled as source provenance, never as a release publication time.

## Failure and recovery

If release evidence is absent, the home page omits its installer download control. Invalid or incomplete release records must not create a guessed download URL. Missing captures remain missing; design references never replace native runtime images.

## Privacy

Published evidence excludes credentials, machine paths and private user content. Only reviewed product captures belong in public documentation.

## Verification

The documentation inventory is generated from complete local articles and wiki chapters. Local source checks validate internal article routes and metadata. Live routes, assets, native workflows and installer behavior require separate runtime evidence.

## Related articles

- [First session](../getting-started/first-session.md)
- [Wiki verification](../../wiki/Verification.md)
