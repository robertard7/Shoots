# SHOOTS
======

Authoritative build system for deterministic, law-driven software execution.

This repository is intentionally strict. If you are looking for a flexible, opinionated, or beginner-friendly framework, this is not it.

---

WHAT THIS IS
------------

Shoots is a sealed execution and orchestration system designed around one core principle:

    The system should be correct before it is convenient.

Shoots is composed of two primary components:

- Shoots.Runtime
  The authoritative execution engine. It defines what can be executed, how commands are described, and how results are produced.

- Shoots.Builder
  The orchestrator. It loads modules, invokes the runtime, emits artifacts, and follows system laws.

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

Shoots defines several non-negotiable laws:

1. Runtime Authority
   The runtime is the sole authority on execution. Nothing bypasses it.

2. No Dead Structure
   Unused methods, dead paths, and unnecessary abstractions are removed, not ignored.

3. Classified Failure
   Failures are categorized (Success, Invalid, Blocked). Errors are not dead ends.

4. No Phantom Success
   Success is reported only when confirmed by build output.

These laws are mechanical, not conventional.

---

WHAT THIS SYSTEM AIMS FOR
-------------------------

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
├─ .ai/                (agent patches and guidance)
├─ tools/              (tooling and handlers)
├─ etc/                (configuration)
├─ ui/                 (user interface)
└─ Agent.md            (contributor guidance)

This layout is intended to stay stable unless explicitly instructed.

ENVIRONMENT PROFILES EXPLAINED
------------------------------

Shoots includes UI-only Environment Profiles that prepare local sandbox folders
and declare optional capabilities for UI affordances.

These profiles:
- Are declarative and inspectable
- Perform only idempotent file operations
- Do not alter runtime determinism or execution logic

Applying a profile is a user-driven UI action and is always optional.

PROJECT WORKSPACES
------------------

Shoots includes a UI-only Project Workspace experience for organizing context.

Workspaces:
- Are pure UI data (name, root path, last opened time)
- Never execute commands or scripts
- Do not depend on any source control provider, database, or external service
- Persist only recent selections in LocalAppData/Shoots/workspaces.json

Workspace isolation is strictly visual: each selection scopes UI context and
environment script previews without mutating runtime behavior or determinism.

PROJECT WORKSPACE GUARANTEES
----------------------------

- Workspaces never execute commands or scripts
- Workspace data stays out of runtime assemblies
- Switching workspaces is reversible and non-destructive
- Recent workspace files can be deleted safely

DATABASE INTENT (NO SHIPPED DATABASE)
-------------------------------------

Shoots does not ship or configure a database by design. The UI exposes a
database intent selector only to record future intent, not to provision
storage, connections, or vendor dependencies.

WHAT SHOOTS DOES NOT AIM TO DO
------------------------------

- Force any source control provider
- Force database usage of any kind
- Hide automation or background execution

NON-GOALS
---------

- AI governance or policy control
- Output validation or tool control
- Security guarantees or compliance certification
- Built-in database servers or migrations
- Automated environment provisioning or installation

WHAT UI DOES NOT DO
-------------------

- Execute commands or scripts
- Modify runtime routing, traces, or determinism inputs
- Depend on source control, databases, or external services
- Touch Runtime.Core directly

---

AI & AUTOMATION NOTES
---------------------
Documentation only. No enforcement implied.
Shoots may be explored, modified, or reviewed using automated tools.
This repository does not define, restrict, or validate the behavior of such tools.
All automation behavior is external and user-controlled.

---

BUILD & VALIDATION
------------------

Builds and tests are typically executed via CI workflows.

Validation results are useful context when a workflow has run and completed.


VALIDATION WORKFLOW (TYPICAL)
-----------------------------

When validation is needed, a typical sequence is:

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
- Clarity over documentation

If you do not agree with those priorities, this repository will be frustrating.

---

LICENSE / OWNERSHIP
-------------------

This repository represents significant original work.

Reuse, redistribution, or derivative systems should respect the author’s intent and licensing terms.

---

FINAL NOTE
----------

Shoots exists to finish systems, not endlessly redesign them.

If something feels "too strict," that is intentional.
