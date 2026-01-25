AI Contracts Frozen
===================

The following AI policy contracts are frozen:

- AiAccessRole
- AiVisibilityMode
- AiPresentationPolicy
- IRuntimeStatusSnapshot

Changes to these types require explicit versioning and updated tripwire tests.

Tripwire tests enforce the contract shape; CI fails when frozen contracts change without versioning.
