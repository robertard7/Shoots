Shoots.Runtime

Shoots.Runtime is a sealed, versioned runtime engine built on .NET 8, designed for deterministic command execution with a strictly enforced runtime boundary.

It is intended to serve as a foundational execution layer for higher-level systems. Once finalized, the runtime is expected to remain stable and unchanged, with all extensibility occurring outside the core.

This project prioritizes correctness, predictability, and long-term stability over flexibility or convenience.

Purpose

Shoots.Runtime provides a controlled execution environment with:

Explicit contracts

Deterministic behavior

Strong isolation between runtime core and extensions

Clear versioning and compatibility guarantees

The runtime intentionally avoids hidden behavior, ambient state, and implicit execution paths.

Key characteristics

Sealed runtime boundary

Contract-only abstractions

Explicit versioning and compatibility enforcement

Safe external module loading

No global state

No implicit dispatch

No dynamic behavior

This runtime is intentionally minimal and conservative.

Repository structure
Shoots.Runtime.Abstractions

Contract definitions only.

Interfaces

Records

Value types

No logic, helpers, parsing, or execution.

Shoots.Runtime.Core

The runtime engine.

Command registry

Execution pipeline

Runtime boundary enforcement

All command execution flows through a single, controlled entry point.

Shoots.Runtime.Loader

External module discovery and validation.

Assembly inspection

Version gating

Safe module instantiation

No coupling to the runtime core.

Shoots.Runtime.Sandbox

A development host used to exercise the runtime during implementation and validation.

Runtime invariants

The following rules are enforced by design and must not be violated:

All command execution flows through RuntimeEngine.Execute

Modules do not call other modules

Modules do not dispatch commands

Modules are pure handlers

No dynamic dispatch

No service locator patterns

No partial runtime state

Violating these constraints breaks runtime guarantees.

Module contract

Runtime modules implement IRuntimeModule and expose handlers that follow a strict input/output contract.

Handler input

RuntimeRequest

RuntimeContext

IRuntimeServices

Handler output

Handlers return a RuntimeResult.

RuntimeResult.Output must be one of the following:

null

An IRuntimeValue

A primitive value (string, bool, number)

The following are not permitted:

Anonymous objects

Dictionaries

Dynamic or loosely typed values

These restrictions ensure predictable, structured output.

External module loading

Shoots.Runtime supports loading external modules from assemblies without coupling them to the runtime core.

Loading process

A directory is scanned for .dll assemblies

Each assembly must declare a runtime module manifest

Manifest metadata is read (no type scanning)

Runtime version compatibility is validated

Module type validity is verified

The module is instantiated

Invalid or incompatible modules are rejected safely.

Modules are either loaded completely or ignored. Partial activation is not allowed.

Module manifest

External module assemblies must declare a manifest at the assembly level.

Example:

using Shoots.Runtime.Loader;

[assembly: RuntimeModuleManifest(
    moduleId: "example.echo",
    moduleType: typeof(EchoModule),
    minMajor: 1,
    minMinor: 0,
    maxMajor: 1,
    maxMinor: 9)]


Assemblies without a manifest are ignored.

This approach avoids reflection scanning and enforces explicit intent.

Version compatibility

Each module declares:

A minimum supported runtime version

A maximum supported runtime version

Modules are rejected if the host runtime version falls outside this range.

This prevents silent incompatibilities as the runtime evolves.

Loader behavior

The loader is intentionally conservative:

Assemblies that fail to load are ignored

Missing manifests are ignored

Version mismatches are ignored

Instantiation failures are ignored

The runtime host always remains in a valid, stable state.

Status

Core runtime architecture is complete and sealed

Abstractions are frozen

Execution semantics are enforced

External module loading is implemented

Runtime builds cleanly and runs via the sandbox host

Once finalized, Shoots.Runtime is intended to remain stable and unchanged.

Intended usage

Shoots.Runtime is designed to be embedded beneath higher-level systems that require:

Deterministic execution

Strong isolation

Explicit extensibility boundaries

Long-term stability

Tooling, orchestration layers, and automation systems should live above the runtime, not inside it.

Status

Shoots.Runtime is considered stable and sealed.

The core runtime is not under active feature development.
New capabilities are expected to live above the runtime as external modules or higher-level systems.

Changes that violate the stability policy will not be accepted.
