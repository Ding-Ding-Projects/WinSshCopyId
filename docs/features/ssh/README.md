# SSH services

Deployment uses immutable requests, outcomes and progress records. SequentialDeploymentService permits 1 to 100 endpoints per operation and permits one active operation per instance. Validate the entire batch before requesting its password. Cancellation prevents subsequent hosts and disposes the current SSH connection. Connection and command timeouts are 25 seconds. Partial outcomes remain available, and failures never contain raw server output or credentials.

Construct SshDeployer with a HostTrustStore and an explicit approval callback. The callback executes on the connection worker and must return a deliberate user decision. A new endpoint is denied without approval. Trust persists by case-insensitive host and port. A changed key is denied even when the callback would approve it. Verification pins the installation fingerprint. Encrypted private keys request their passphrase through an operation-only callback. Only one validated public key line is sent to authorized_keys.

SshInput builds an identity-specific command and quotes configuration paths. Config insertion validates endpoint, alias and username, preserves existing text, and never replaces an existing matching alias. Target parsing rejects malformed ports and unexpected fields rather than silently selecting another endpoint.

Unit tests use fake transport and temporary trust files. No test connects to an SSH endpoint. Current-host and real-server behavior require separate runtime verification.
