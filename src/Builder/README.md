<p align="center">
  <strong>[Shoots.Builder]</strong><br />
  Deterministic build orchestration on top of Shoots.Runtime
</p>

---

## What this is

This project is a **build and orchestration system** built **on top of**  
[`Shoots.Runtime`](https://github.com/robertard7/Shoots.Runtime).

It translates **structured or text-driven requests** into **reproducible build artifacts**
by executing commands through a sealed, deterministic runtime.

This repository is **not** a runtime.
It is **not** a foundation.
It is a system that *depends on* a foundation that is already frozen.

If this project breaks, it can be rewritten.
If Shoots.Runtime breaks, everything breaks — which is why it is sealed elsewhere.

---

## Design goals

- Deterministic execution
- Explicit orchestration
- Reproducible artifacts
- Headless-first operation
- Clear separation between:
  - planning
  - execution
  - artifact materialization

This project intentionally avoids “magic” behavior.
Everything that runs must map to a runtime command.

---

## Architecture overview

[ User / API / CLI / Codex ]
↓
Orchestration Layer
↓
Shoots.Runtime
↓
Deterministic Commands
↓
Artifacts


This repo lives **above** the runtime boundary.

It may:
- plan
- sequence
- retry
- cache
- observe

It may **not**:
- execute commands directly
- bypass the runtime
- mutate runtime behavior
- depend on implicit execution

---

## Artifacts

A build produces an **artifact set** identified by content, not time.

Typical structure:

artifacts/
<hash>/
tree.json
preview/
output.zip


Artifacts are:
- reproducible
- cacheable
- content-addressed
- immutable once produced

Re-running the same request should produce the same artifact hash.

---

## Execution model

1. Input is received (text, structured request, or API call)
2. A plan is produced (explicit, inspectable)
3. The plan is executed **only through Shoots.Runtime**
4. Outputs are collected and materialized as artifacts
5. Results are returned via API / CLI / UI

There are no hidden execution paths.

---

## Headless-first

This project is designed to run without a UI.

All functionality must be accessible via:
- API
- CLI
- programmatic integration

Any UI is an optional adapter, not the system itself.

---

## Relationship to Shoots.Runtime

Shoots.Runtime is:
- sealed
- versioned
- deterministic
- owned
- not modified by this repo

This project:
- consumes runtime contracts
- targets runtime commands
- adapts runtime output into higher-level workflows

If a feature would require changing Shoots.Runtime,
**it does not belong here**.

---

## Status

This repository is under active development.

Its scope, APIs, and behavior may evolve.
Breaking changes are allowed here.

Shoots.Runtime is the stability anchor.
This project is intentionally flexible by comparison.

---

## License

MIT © Robert Ard  
(Runtime core licensed separately under Apache-2.0)
