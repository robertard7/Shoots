# Toolpacks

Toolpacks are plug-in folders that describe tool metadata without executing tools.
Each toolpack declares a tier and ships with a `toolpack.json` manifest.

Tiers:
- Public: default metadata surface for regular users.
- Developer: additional build and workflow metadata.
- System: OS-level stubs that are opt-in per workspace.

The manifest schema lives at `toolpacks/schema/toolpack.v1.json`.
Toolpacks are descriptive only and do not execute or authorize actions.
