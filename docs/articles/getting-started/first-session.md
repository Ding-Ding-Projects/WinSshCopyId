# Start with the installed application

## Behavior

WinSshCopyId is a native Windows x64 SSH key deployment application. Use the installed application for endpoint profiles, key selection, explicit host-trust decisions, deployment and verification. This documentation website cannot connect to an SSH host, install a key or run a local server.

## Configuration

Prepare a host name, SSH port, user name and a supported public identity. Review the fingerprint through an independent trusted channel before accepting a first connection. Preserve existing profiles and identities. Never include private keys, passwords or passphrases in portable profiles.

## Failure and recovery

A changed host key stops authentication. Investigate the change before editing trust records. Cancellation stops subsequent work but cannot undo a server change that may already have happened. An uncertain installation needs reconciliation rather than an automatic reinstall.

## Privacy

Credentials belong to operation-only prompts or the operating-system credential store. Do not paste credentials into logs, reports, screenshots or this website. Browser preferences remain separate from native application data.

## Verification status

Source implementation and local service checks do not establish installed runtime behavior. Consult the delivery status and the individual feature articles for the exact evidence boundary. No installer download is offered until an immutable release manifest is validated.

## Related articles

- [Understand delivery evidence](../reference/delivery-evidence.md)
- [Documentation privacy and preferences](../reference/site-preferences.md)
- [Wiki home](../../wiki/Home.md)
