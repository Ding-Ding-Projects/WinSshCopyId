# Loopback SSH acceptance

The production transport has a disposable loopback integration test under tests/WinSshCopyId.Services.Tests. See its README for pinned setup, exact assertions and teardown.

The fixture establishes genuine SSH negotiation and password/public-key authentication with generated test identities. It covers explicit trust before authentication, persisted trust, changed host keys, duplicate installation, Ed25519 and RSA verification, cancellation, the configured timeout and partial batch outcomes. The fixture's narrow command emulator does not prove POSIX shell execution or OpenSSH sshd interoperability.

The acceptance work repaired cancellation latency by using the SSH library's cancellation-aware connection and command operations. It also prioritizes existing RSA SHA-2 algorithms before the legacy RSA signature probe, avoiding an immediate disconnect on modern servers. Public service signatures remain unchanged.

Native credential vault behavior, real server authorized_keys semantics and GUI-driven deployment remain separate acceptance requirements. No real user keys, credentials, service installation or global host configuration are used by this fixture.
