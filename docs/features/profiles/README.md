# Profiles and credentials

Settings schema version 2 remains compatible with earlier fields. Saves use a temporary sibling, flush to storage and atomic replacement. Portable exports include only connection settings and a product/schema marker. Credentials and appearance preferences are excluded. Import clears credentials even from older files; ApplyProfile preserves the receiving user's theme and auto-save preference.

ICredentialStore exposes Get, Set and Delete for named credentials. WinCredentialStore uses Windows Credential Manager generic credentials under the WinSshCopyId/ target prefix. Access errors are reported rather than treated as absent credentials. Services must retrieve credentials only for the current operation and must never put plaintext into profile exports, logs or operation history.

MigrateLegacyCredential writes and reads back the protected destination before saving settings without the legacy blob. If settings persistence fails, the legacy in-memory value is restored and its original file remains recoverable. The overload accepting ICredentialStore uses the stable ssh-password key. Call migration before adopting the new credential store, then use that store for subsequent operation callbacks.

Managed strings cannot guarantee immediate plaintext erasure. Neither password nor passphrase is stored in deployment requests or outcomes. Runtime credential-vault migration and host approval dialogs require separate current-user verification.
