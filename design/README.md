# Workspace design references

Design baseline: `d9b3ce8d2e64fc2f1c978f4a97d4aaecacb8457f`. Target: native Windows App SDK / WinUI 3. These files describe intended behavior; they are not screenshots or evidence that the native application implements it.

- [Implementation handoff and component contract](workspace-spec.md)
- [Deterministic interactive reference](workspace.html)
- [Screen, state, and acceptance inventory](screens.json)

Open `workspace.html` locally. No network, external assets, dependency installation, or production actions are required. URL parameters select reproducible states: `?screen=connections&theme=light&lang=en&state=ready`; screens are enumerated in `screens.json`, themes are `light|dark`, languages are `en|yue|both`, and states are `ready|empty|working|error|locked`. The preview controls change only reference state. A reference action reports its design intent instead of pretending to execute SSH, modify files, install a runtime, or manipulate a native window.

Reference coverage is deliberately separate from implementation acceptance. It establishes the shell, density, hierarchy, forms, destination architecture, and state treatments. Detailed appearance, animation, tab management, regex, and server capabilities are specified in the handoff but require real native components and their own verified states. The HTML uses local semantic reference primitives, not a third-party Material implementation; it does not establish Material component provenance for the shipped product. No generated or stock image is used as product evidence.
