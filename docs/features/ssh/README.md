# SSH services

Deployment uses immutable requests, outcomes and progress records. SequentialDeploymentService permits 1 to 100 endpoints per operation and permits one active operation per instance. Validate the entire batch before requesting its password. Cancellation prevents subsequent hosts and disposes the current SSH connection. Connection and command timeouts are 25 seconds. Partial outcomes remain available, and failures never contain raw server output or credentials.

Construct SshDeployer with a HostTrustStore and an explicit approval callback. The callback executes on the connection worker and must return a deliberate user decision. A new endpoint is denied without approval. Trust persists by case-insensitive host and port. A changed key is denied even when the callback would approve it. Verification pins the installation fingerprint. Encrypted private keys request their passphrase through an operation-only callback. Only one validated public key line is sent to authorized_keys.

SshInput builds a PowerShell identity-specific command using single-quoted literals with escaped apostrophes, and separately quotes configuration paths. Public-key wire parsing accepts Ed25519, RSA and ECDSA NIST P-256/P-384/P-521 bodies, checks bounded required fields and rejects trailing bytes or unsupported algorithms. Parser-only tests never execute generated connection text. Config insertion validates endpoint, alias and username, preserves existing text, and never replaces an existing matching alias. Target parsing rejects malformed ports and unexpected fields rather than silently selecting another endpoint.

Unit tests use fake transport and temporary trust files. No test connects to an SSH endpoint. Current-host and real-server behavior require separate runtime verification.

## Operation outcomes and retry

Every requested target receives an outcome, including targets not started after cancellation. Installation and verification states are separate. A completed installation retains its fingerprint when verification fails or cancellation arrives. An interrupted install without a returned result is uncertain; cancellation never claims to undo server changes. Verification failures offer verification-only retry. Only a transport-declared failure known to precede installation offers installation retry. Uncertain mutation and changed host keys require reconciliation instead of automatic retry.

Host approval includes the negotiated algorithm. Changed-key diagnostics contain the endpoint, expected fingerprint and observed algorithm/fingerprint; the approval callback is never offered a changed key. UI callbacks run on the connection worker and must bridge to the dispatcher with bounded cancellation.

## Explicit key generation

KeyManager.GetSupportedAlgorithms reports managed RSA plus Ed25519 when an actual ssh-keygen executable is discovered. GenerateKey accepts an explicit destination basename, algorithm, comment, log callback and cancellation. It generates in a private sibling staging directory, parses the public key, checks that the private key matches, and verifies private-key ownership and a protected owner-only ACL. Existing private or public destinations are never overwritten. External ssh-keygen is bounded to 30 seconds and terminated only when owned cancellation or timeout occurs.

The pair is staged and validated before two no-overwrite moves. Two separate sibling files cannot become visible in a single filesystem rename. A publication failure rolls back only files whose hashes still match the generated pair. This provides rollback rather than atomic simultaneous visibility; no atomic two-file publication claim is made. Temporary-only tests cover discovered algorithms, permission readback, existing-destination preservation and cancelled generation. No existing user identity is modified by the tests.
