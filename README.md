# SHOOTS
======

Authoritative build system for deterministic, law-driven software execution.

This repository is intentionally strict. If you are looking for a flexible, opinionated, or beginner-friendly framework, this is not it.

---

WHAT THIS IS
------------

Shoots is a sealed execution and orchestration system designed around one core principle:

    The system must be correct before it is convenient.

Shoots is composed of two primary components:

- Shoots.Runtime
  The authoritative execution engine. It defines what can be executed, how commands are described, and how results are produced.

- Shoots.Builder
  The orchestrator. It loads modules, invokes the runtime, emits artifacts, and enforces system laws.

Both components live in this repository as submodules and are versioned together to eliminate drift.

---

WHAT THIS IS NOT
----------------

- Not a script runner
- Not a plugin playground
- Not a self-modifying AI system
- Not a framework that guesses, retries silently, or smooths over errors

If something fails, it fails loudly and with classification.

---

CORE LAWS
---------

Shoots enforces several non-negotiable laws:

1. Runtime Authority
   The runtime is the sole authority on execution. Nothing bypasses it.

2. No Dead Structure
   Unused methods, dead paths, and unnecessary abstractions are removed, not ignored.

3. Classified Failure
   Failures are categorized (Success, Invalid, Blocked). Errors are not dead ends.

4. No Phantom Success
   Nothing is considered successful unless confirmed by the build runner.

These laws are enforced mechanically, not by convention.

---

WHAT THIS SYSTEM GUARANTEES
---------------------------

- Deterministic execution based on the committed runtime and plan inputs
- Provider output cannot advance routing or override graph authority
- Failures surface as classified runtime errors with traceability
- Replays remain stable when inputs and runtime versions match

---

WHAT THIS SYSTEM WILL NOT DO
----------------------------

- Execute with untrusted or mutable routing authority
- Accept configuration that alters node choice or graph traversal
- Mask failures, auto-retry without instruction, or infer success

---

REPOSITORY STRUCTURE
--------------------

Shoots/
├─ Shoots.Runtime/     (execution engine)
├─ Shoots.Builder/     (orchestrator)
├─ .ai/                (agent patches and policy enforcement)
├─ tools/              (tooling and handlers)
├─ etc/                (configuration)
├─ ui/                 (user interface)
└─ Agent.md            (AI agent operating law)

Do not restructure this layout unless explicitly instructed.

---

AI / CODEX USAGE
----------------

This repository includes a strict Agent.md file.

Any AI agent interacting with this repo must:

- Operate only within the repository root
- Produce unified diffs only
- Never claim execution or fabricate results
- Rely exclusively on the Windows self-hosted GitHub Actions runner for validation

Agents that violate these rules produce invalid output by definition.

---

BUILD & VALIDATION
------------------

All builds and tests are executed via GitHub Actions using a Windows self-hosted runner.

Codex and other agents are not allowed to execute code locally or in containers.

The runner is the single source of truth.


CODEX COMMAND CONTRACT
----------------------

Codex validation must follow this exact sequence:

    dotnet restore
    dotnet build Shoots.sln -c Release
    dotnet test Shoots.sln -c Release

REGRESSION TRIPWIRE
-------------------

If anything regresses, re-run the contract above as the single health check.

---

CONTRIBUTIONS
-------------

Contributions are not accepted casually.

This project prioritizes:
- Determinism over convenience
- Explicit structure over flexibility
- Enforcement over documentation

If you do not agree with those priorities, this repository will be frustrating.

---

LICENSE / OWNERSHIP
-------------------

This repository represents significant original work.

Reuse, redistribution, or derivative systems must respect the author’s intent and licensing terms.

---

FINAL NOTE
----------

Shoots exists to finish systems, not endlessly redesign them.

If something feels "too strict," that is intentional.
