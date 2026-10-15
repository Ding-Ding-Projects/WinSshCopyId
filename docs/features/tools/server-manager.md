# Server manager services

The server services expose immutable instance and target records, an atomic local registry, lossless property editing, official Java metadata discovery, reviewable lifecycle plans, and verified directory backup/restore for explicitly adopted stopped local-process instances. Construction and discovery never launch a server or adopt existing processes.

## Source discovery and capabilities

`ServerCatalogue.RefreshJavaAsync` reads the official Mojang version manifest, retaining every returned release, snapshot and historical entry. A selected entry is resolved only after its metadata SHA-1 matches. Missing dedicated-server downloads return unavailable; missing Java runtime information remains unknown. Returned download descriptors include exact byte count and digest. The catalog checks bounded response sizes, timeouts, approved HTTPS origins and the final response URI. The default constructor disables redirects and proxies. The injected-client constructor exists for controlled transports and tests; callers must disable redirects on any injected live HTTP client because checking a final URI cannot prevent that client from already following an unauthorized redirect.

The snapshot deliberately reports incomplete coverage: Java metadata entries do not prove that every server distribution exists. Historical Bedrock packages, third-party flavors, plugins, mods, packs and version-specific setting descriptors require additional source and runtime acceptance. No default list is represented as exhaustive.

## Configuration and persistence

`ServerProperties` preserves comments, unknown properties, ordering and original newline style while applying typed validated port changes. It rejects line injection. This is literal `key=value` support, not a full Java-properties escape/continuation implementation. `ServerRegistry` uses schema-versioned immutable records, duplicate identity checks, a 16 MiB limit and temporary-file promotion. Instance extension values survive a round trip. Private instance state belongs under the user's application-data directory, never the repository.

## Four target boundaries

The target model includes local process, local Docker, SSH process and SSH Docker. Local Java/Bedrock start plans require explicit adoption, EULA consent, absolute paths and distribution receipts. Docker discovery produces only a label-filtered read-only command. Starts require an immutable image digest and remain blocked pending daemon, image, mount and entrypoint acceptance. SSH requires approved host-key identity and remains blocked until a structured remote-agent transport exists. No user-entered remote shell text is generated. Stop/restart remain blocked until an exact distribution-specific graceful management transport is negotiated. A PID alone never authorizes termination.

`ServerPlan.CanExecute` concerns the plan's current blockers only. No execution adapter is enabled by this implementation. Commands must never be run merely because a plan can be displayed. Runtime hash verification, process ancestry, ownership receipts, health, loss/reconnect and graceful stop acceptance remain required before enabling execution.

## Backup and restore

Backups require adopted local-process instances and fresh stopped observations. Links and reparse points are rejected. Storage is bounded to a configurable 64 GiB and one million entries by default. Each copied member has a SHA-256 receipt. Backups are promoted only after successful creation. Restore verifies all members, stages and independently verifies the restored bytes, then renames the old instance directory into a retained rollback directory before promotion. Cancellation before promotion leaves the existing directory in place. The original rollback directory is never automatically removed. These are file operations, not proof of a live server's stopped state; the owner must supply a trustworthy current observation.

## Verification and remaining work

The dependency-free `tests/WinSshCopyId.Tools.Tests` executable checks property preservation/injection rejection, registry persistence, blocked unapproved launches, untrusted SSH, backup/restore/rollback byte preservation, tampered-backup rejection, running-instance rejection and Java metadata digest validation with a fake HTTP transport. It launches no real server or service. Full target execution, Java/Bedrock runtime matrices, plugin/mod installation, world management, upgrades, remote backups, networking, real GUI acceptance and complete historical compatibility remain unverified and incomplete.

Official sources: [Java manifest](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json), [Bedrock server downloads](https://www.minecraft.net/en-us/download/server/bedrock), [Bedrock dedicated server documentation](https://learn.microsoft.com/en-us/minecraft/creator/documents/bedrockserver/getting-started).
