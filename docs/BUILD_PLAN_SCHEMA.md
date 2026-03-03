# Build Plan Schema

Shoots builder scaffolds a minimal deterministic plan contract for builder scenarios.

Schema file: `etc/schemas/build_plan.schema.json`

## Required fields

- `planHash`: deterministic content hash from semantic inputs.
- `inputs`: language/name/description.
- `provider`: provider kind and provider config hash reference.
- `environment`: environment id and descriptor hash reference.
- `steps`: deterministic ordered list of step records.

## Determinism rules

- `planHash` must not include timestamp fields.
- Step ordering must be stable and deterministic.
- Replays compare `planHash`, provider hash, environment hash, trace hash, and output manifest hash.


## Step contract v1

Each `steps[]` item includes:
- `stepId` deterministic id
- `kind` one of `SelectTool|RunTool|Verify|EmitArtifact`
- `toolId` for tool steps
- `args` sorted-key object
- optional `inputs`, `outputs`, `expects`
