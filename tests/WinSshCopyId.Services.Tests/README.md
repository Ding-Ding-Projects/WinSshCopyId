# Disposable loopback SSH acceptance

This test uses the production SshDeployer, SshDeploymentTransport and SequentialDeploymentService against a pinned Paramiko server bound only to 127.0.0.1 on an operating-system-selected high port. It generates disposable host keys in memory and client identities in a temporary directory. It never reads OS accounts, user credentials or existing identities, installs a service, changes global settings, or opens the firewall.

## Setup and run

Use the installed Python interpreter to create `.fixture-env` in this directory, then install only the pinned requirements from the official PyPI index:

```powershell
python -m venv .fixture-env
.fixture-env/Scripts/python.exe -m pip install --index-url https://pypi.org/simple -r requirements.txt
dotnet test --nologo
```

The test-only global.json selects installed .NET SDK 8.0.424. Missing tools or dependencies are test failures, never successful skips. The virtual environment and process receipts are ignored. Restore and test execution are local test operations, not product build or installer evidence.

## Acceptance boundaries

The single ordered acceptance test checks unknown-host rejection before password authentication, explicit approval before authentication, persisted host trust, duplicate installation, actual Ed25519 and RSA public-key authentication, changed-host rejection before authentication, verification failure preserving installation facts, active command cancellation, the production 25-second command timeout, and partial batch outcomes after cancellation. Active command cancellation must raise OperationCanceledException. The timeout check first observes a new stalled-command event, then requires SshOperationTimeoutException within 24 to 30 seconds, below the fixture stall close at 35 seconds. An outer wait timeout or another transport exception cannot satisfy these assertions. Generated connection commands are not executed by a local shell.

SSH negotiation, encrypted transport and signature authentication are genuine Paramiko protocol operations. The command handler deliberately emulates only the expected installer response and verification echo. It stores authorized public identities in process memory. It does not execute the POSIX script, model POSIX permission behavior, or prove OpenSSH sshd interoperability, real authorized_keys filesystem semantics, encrypted-key UX or GUI behavior.

Each fixture is bounded to 120 seconds. The test records launcher and actual listener PID/start time, terminates only the owned process tree, verifies listener process termination and confirms the loopback port is closed. Receipts under `.fixture-output/` retain timestamped run variants, including incomplete attempts. Temporary generated identities are removed only after server teardown. No keys, credentials, machine paths or private receipts are committed.

## Repairs established by the fixture

The initial loopback run exposed that cancellation around synchronous SSH execution could wait for the command timeout. Production connection and execution now use the SSH library's cancellation-aware async operations while retaining the public synchronous wrappers.

The initial RSA fixture run also stopped because the library's default key algorithm order probes obsolete ssh-rsa before its existing SHA-2 choices. Modern servers may disconnect immediately on that probe. Production verification now prioritizes the existing SHA-2 choices using the supported IPrivateKeySource interface. This enables no additional algorithm and the fixture verifies a generated RSA identity without weakening the server.
