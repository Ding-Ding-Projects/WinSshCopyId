# Reviewed x64 payload manifests

These public metadata files contain source URLs, exact archive lengths/digests and every archive file member's length/SHA-256. Binary payloads remain in the application build cache and are never committed. No model weights were selected or acquired.

| Payload | Version | Architecture | Verified source | State |
|---|---|---|---|---|
| Ollama portable runtime | 0.40.4 | Windows x64 | Official `ollama/ollama` release; archive digest matches release asset; 82 file members hashed; valid native Authenticode and exact signer pin | Managed acquisition only; not bundled by default, not installed or launched |
| qpdf | 12.4.2 | Windows x64 | Official `qpdf/qpdf` release; archive digest matches release asset; 291 members hashed | Payload proven; isolated PDF adapter integration and runtime acceptance pending |
| Pandoc | 3.12.1 | Windows x64 | Official `jgm/pandoc` release; archive digest matches release asset; four members hashed | Payload proven; isolated document adapter integration and runtime acceptance pending |

Preserve each complete license/notice inventory during packaging. Ollama and its archive include third-party licenses. qpdf uses Apache-2.0 and includes third-party notices. Pandoc uses the GNU GPL version 2 or later; distribution must satisfy its license and corresponding-source obligations. A manifest is neither a license review nor runtime evidence.

`New-OllamaPayloadManifest.ps1` reproduces the pinned x64 Ollama inventory through `gh release download`, validates the archive before opening it, hashes every file member and inspects the extracted executable's signature without launching it. The reproduction script is an explicit build/review operation, never an automatic model or runtime installation.

The installer consumes `OllamaPinnedPayload.LoadAsync`, its exact signer callback and the reviewed JSON. A cached archive can be admitted only after its exact length and hash match. Native trust verification uses cached trust data and performs no network retrieval. Catalog and installer verification have separate verdicts.

FFmpeg, ImageMagick, LibreOffice and 7zip manifests are not yet supplied. Their absent capabilities remain disabled with their exact reason. Do not enable a tool from PATH or treat a release-page link as a verified bundled engine.
