# Info-icon guidance inventory

| Inventory identity | Current scope | Source status | Runtime status |
| --- | --- | --- | --- |
| `info-icon-guidance` | 23 primary connection, key, result and settings controls | Target-specific descriptors, above-control icon entry, optional inline panel and isolated synthetic preview implemented | Unverified; native rebuild and interaction still required |

The hand-written identifier list is `ControlGuidance.RequiredIds` in `UI/ControlGuidance.cs`. It is separate from regex and general help coverage. `Validate` rejects an omitted descriptor or missing explanation/example metadata. `ValidateAttachments` rejects an omitted registered entry after the primary pages are constructed. The UI logic runner deliberately removes every required descriptor and every attached-entry identity, then checks each rejection. Its 61 passing behavioral checks include the earlier profile/cancellation checks; this count is not a native UI verdict.

Covered targets: connection display name, hosts, username, port, password, credential-memory choice, installation, save; profile import/export; public-key choice/generation/export; operation cancellation; result retry/copy/export; application display name; both funny-level sliders; reduced motion; accessibility-entry visibility; settings save.

Each entry is a locally drawn info symbol in a 44 by 44 logical-unit button directly above its control. The localized accessible name and tooltip identify that control. Expanded/collapsed status is exposed through accessible status/help text. The optional panel sits in content flow, leaves the real control usable and returns focus to the entry on close. Its draft begins from a synthetic example, produces a visible validation or explicitly synthetic preview, resets independently, and is destroyed on close. It never reads the real control's current value or invokes its production action. Consequential-action examples are previews, not completed operations.

## Explicit remaining work

This is a preservation milestone, not complete guidance acceptance. Theme/language/density choice controls, navigation, search fields and builders, native picker internals, generated-key dialog fields, confirmation controls, read-only results and nested editor controls are not yet covered by this inventory. Full complex workflow step progression, Back and recovery paths, all localized playground result text, guidance search with its own regex builder, command-palette help teleport and full explanation/default-provenance depth remain incomplete. The playground's own controls use ordinary labels and currently lack their complete contextual explanations.

Native compilation and source-bound interaction have not verified above-control placement, focus, screen-reader state, narrow layout, all language modes, Reset, independent concurrent drafts, or absence of production effects. The omission tests validate the declared primary inventory, not discovery of every rendered control. These gaps must remain visible until their actual implementation and evidence exist.
