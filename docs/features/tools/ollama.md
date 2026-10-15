# Local Ollama manager

## Available backend

`WinSshCopyId.Tools.Ollama.OllamaClient` connects only to `http://127.0.0.1:11434/`. It disables proxies and redirects. It supports version health, installed tags, running models, metadata, pull, delete, copy, generation and chat through the documented local API. Requests accept cancellation. Metadata requests have a 30-second bound and 8 MiB response limit; streams have a 1 MiB record limit, 64 MiB total limit and operation deadlines (one hour for pulls, ten minutes for inference). Streaming completion must be present. Errors are sanitized and never include response bodies or prompt text.

Generation uses typed bounded temperature, context and output settings. History is limited to 256 messages and 262,144 characters per request. Attachments and tools are not accepted, because capability admission and their resource controls are not implemented. There are no cloud credentials, proxies, unofficial catalog sources or invented sample models.

## Durable local operations

`OllamaStateStore` stores immutable queue, chat and catalog snapshots in the current user's local application-data directory. Writes are serialized and atomically replace the previous file. State size is capped at 16 MiB; incompatible or excessive files remain intact and produce an explicit error. Saved chat content is local plaintext: do not enter credentials or sensitive data. Ordinary redacted export omits all model, title and message content rather than pretending arbitrary model output can be automatically sanitized.

`OllamaPullQueue` processes one pull at a time. It records queue and terminal states, reports API-supplied byte counters, verifies exact installed tags after completion, skips exact tags already present, marks interrupted operations honestly and supports explicit retry. Cancellation stops the client request and preserves installed models. Cancellation does not claim the server deleted partial cached layers. No automatic model deletion occurs. Queue execution must follow a visible user review of network transfer, known sizes, free storage and unknown estimates. The backend does not supply that UI or storage preflight yet.

## Catalog and hardware evidence

The initial catalog is unavailable, with zero invented entries. A local tag inventory is never presented as an exhaustive official catalog. Catalog snapshots retain refresh and last-success timestamps, immutable source identity, page count and completeness; a complete snapshot requires provenance. Official exhaustive catalog acquisition, pagination and tag completeness verification are not implemented. Cached local state remains readable while offline. The caller must display age, completeness and offline state alongside cached entries.

Hardware fit is explicitly `Unknown`. No model-name heuristic or missing-value-to-zero assumption is used. Detection of RAM, usable VRAM, driver support, disk capacity and exact context overhead is still required before an evidence-backed verdict can be offered.

## Managed installation and launch boundary

`OllamaManagedPlans` exposes two typed launch shapes, fixed arguments and a fixed loopback environment. Arbitrary commands, script text and caller-controlled environment entries are not accepted. Preview verifies path shape and records identity and listener prerequisites. Every preview remains disabled because process execution, OS identity admission, health verification and rollback have not been implemented. Profile snapshots support inspection and in-memory restore only; they do not claim restoration of files or runtime settings.

The architecture-specific installation plan lists missing official payload identity, digest, publisher, member inventory, storage, preservation, cancellation, extraction and atomic promotion evidence. It performs no downloads, installation, process launch, service changes or PATH changes. A prepared plan is not an installed runtime. Existing installations and model stores are untouched.

## Integration and remaining acceptance

This backend is intended for immutable UI binding. The tools UI must provide real pickers, offline help, contextual recovery, model filters, the complete regex builder, per-model and aggregate batch estimates, explicit destructive confirmation, chat rename/search/history controls, capability-gated attachments and accessible operation status. Backend API availability alone is not a finished manager.

Remaining work includes exhaustive official catalog refresh, hardware/resource discovery, storage admission before pull, configurable bounded parallelism, complete reviewed export, durable profile snapshots, managed installer execution, owned-process lifecycle and automatic rollback. No real Ollama operation, installation, GUI verification or runtime acceptance has been run for this implementation.

## API references

- [Official local API introduction](https://docs.ollama.com/api/introduction)
- [Chat endpoint and message format](https://docs.ollama.com/api/chat)
- [Pull endpoint](https://docs.ollama.com/api/pull)
