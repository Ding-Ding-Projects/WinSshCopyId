# Local file conversion services

The catalog contains Documents/PDF, Images, Audio, Video, Archives, Structured Data/Spreadsheets, Code/Text and Binary Encodings. Unsupported engines remain visible with an exact unavailable reason. No executable is discovered through PATH and no external process or network service is launched.

## Implemented operations awaiting availability proof

All adapters default to `Available=false`, including managed implementations, until genuine packaged offline operation and isolation receipts exist. `ConvertAsync` enforces this disabled state. `VerifyManagedImplementationAsync` exposes only the bounded managed implementation for explicit verification; it must not be used to enable a user-facing format or claim packaged readiness. Source signature lists are immutable.

The managed implementation formats JSON, converts CSV into JSON arrays of string rows, preserves strict UTF-8 text, encodes and decodes Base64, and compresses or expands a single GZip stream. Detection examines bytes, including PDF, PNG, JPEG, ZIP, GZip, WAVE and MP4 signatures. Signatures are routing hints, never claims that an entire media file is valid. CSV is explicitly selected from text because delimiter characters alone cannot establish a CSV schema.

Each conversion reads at most 16 MiB, permits at most 32 MiB output, limits JSON depth to 64, and limits CSV rows and columns. Source files remain unchanged. The caller supplies the destination following an explicit conversion action and disclosure. Existing destinations are rejected. Output is written beside its destination, reopened and compared, then renamed without overwrite. Cancellation removes temporary output before promotion. Destination free space is checked before writing.

## Durable queue

`ConversionQueue` stores one bounded JSON record per file in a caller-selected private application-data folder. Enqueue incrementally rather than collecting a complete file list. `ReadPage` reads at most 200 records, and `ProcessAsync` processes sequentially with bounded per-file memory and no artificial total-file cap. Outcomes are persisted per file. Interrupted running records require review and re-enqueue instead of unsafe automatic replay. Existing output is never assumed to prove a successful prior conversion. The queue contains private local paths and must not be exported or logged.

## Current limitations and verification

PDF operations, office formats, image/audio/video transforms and ZIP/7z extraction remain disabled. There is no packaged isolated decoder, external executable receipt, CPU/memory sandbox or proven offline package receipt. Managed adapters run in process under strict byte limits; hostile-input process isolation remains incomplete. Catalog localization and GUI integration belong to the consuming interface. This service work alone proves neither packaged availability nor complete converter acceptance.

Pause takes effect between files. Active cancellation uses the run cancellation token. Queue processing has one writer per instance; callers must retain a single queue instance per private directory. Durable storage uses atomic rename but does not promise power-loss durability. Pagination has stable membership only while discovery is idle; concurrent additions may change page order. Automated runtime acceptance remains pending.
