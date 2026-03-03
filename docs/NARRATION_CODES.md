# NARRATION CODES

| code | phase | severity | stable summary | expected artifactRefs | recommended operator action |
|---|---|---|---|---|---|
| `startup.begin` | startup | info | Starting builder smoke scenario | run folder | verify project scaffold paths |
| `plan.materialize.start` | plan | info | Materializing plan | run folder | inspect `plan/plan.json` |
| `plan.read` | plan | info | Reading plan scaffold | run folder + plan path | verify plan exists |
| `plan.hash` | plan | info | Plan hash loaded | run folder | compare `hashes.json` plan hash |
| `provider.read` | provider | info | Reading provider scaffold | run folder + provider path | verify `provider.json` |
| `provider.hash` | provider | info | Provider hash loaded | run folder | compare provider hash |
| `env.read` | env | info | Reading environment scaffold | run folder + env path | verify env selected file |
| `env.hash` | env | info | Environment hash loaded | run folder | compare env hash |
| `execute.begin` | execute | info | Executing deterministic builder steps | run folder | inspect step list |
| `execute.step.begin` | execute | info | Running step | run folder + stepId | inspect tool request |
| `tool.start` | tool | info | Starting tool execution | run folder + stepId | inspect `tool/<stepId>/request.json` |
| `tool.complete` | tool | info | Tool execution completed | run folder + stepId | inspect `tool/<stepId>/result.json` |
| `execute.step.end` | execute | info | Step completed | run folder + stepId | verify step outputs |
| `execute.end` | execute | info | Execution completed | run folder | inspect `result.json` |
| `finalize.write_artifacts` | finalize | info | Wrote run artifacts | run folder | inspect run artifacts manifest |
| `replay.begin` | replay | info | Starting replay | run folder | inspect replay inputs |
| `replay.inputs` | replay | info | Loaded replay inputs | run folder | verify hashes and run id |
| `replay.hash.compare` | replay | info | Compared replay hashes | run folder + diagnostics | inspect mismatch keys |
| `replay.result` | replay | info | Replay completed | run folder + replay json | inspect replay pass/fail |
| `error` | startup/plan/provider/env/execute/tool/finalize/replay | error | Failure | run folder + failing artifact refs | open `failure-fingerprint.json` then narration tail |
