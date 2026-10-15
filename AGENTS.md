# WinSshCopyId contributor guidance

This repository contains a native WinUI 3 application targeting Windows x64.
The supported production entrypoints are `build.bat` and `build-installer.bat`.
Their pinned manifest is `build/manifest.json`. Tests run locally, separately
from the hosted build and release workflow.

## Agent conversation vocabulary

Agents working in this repository use the shared private conversation vocabulary defined in
the maintainer's canonical agent instructions for every reply, including chat prose, visible
reasoning, status lines, and task or session titles. Read those instructions and apply them
in full. They are deliberately not restated here, because this file is part of this
repository's public record.

That vocabulary never appears in anything published. Keep it out of commit messages, branch
names, code, code comments, test names, documentation, this file, issues, pull requests,
discussions, release notes, published sites, logs, and every other file in this repository.
Published writing uses ordinary professional English, and exact technical identifiers always
keep their literal spelling. The single documented public-safe exception is named in those
canonical instructions; do not infer any other.

Scan any text bound for a public surface against that vocabulary before publishing it. A
reviewer cannot tell a correct release note from a leaking one by reading it, so the scan is
a step, not a habit.

## Product boundaries

- Preserve the `WinSshCopyId` installed identity and existing profile compatibility.
- Keep private keys and credentials out of portable profiles, logs and captures.
- Require an explicit decision before trusting a new SSH host key; changed keys stop authentication.
- Design the interface around actionable SSH workflows and actual operation state.
- Keep design references distinct from evidence of the native application.
- Record unfinished capabilities and unrun checks explicitly in the roadmap and handoff.
