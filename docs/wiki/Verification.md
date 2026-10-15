# Verification and delivery

The supported production entrypoints are `build.bat` and `build-installer.bat`. Builds, local checks, packaging, publication, installation and runtime acceptance are separate evidence stages. The installer uses Squirrel.Windows and is unsigned. An unknown-publisher warning is not evidence of malicious behavior or of verified trust.

A release needs its exact source, immutable tag and expected downloadable assets verified. A documentation download control remains absent until a validated release record exists. The live website and its internal assets require their own delivery check.

Related: [Delivery evidence](../articles/reference/delivery-evidence.md), [Home](Home.md).
