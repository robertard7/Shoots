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
| `retrieval.start` | retrieval | info | Starting retrieval step | run folder + retrieval | inspect retrieval request |
| `retrieval.slice.start` | retrieval | info | Building retrieval slice | run folder + slice refs | verify include/exclude globs |
| `retrieval.slice.done` | retrieval | info | Retrieval slice built | run folder + retrieval result | verify slice hash |
| `retrieval.rank.start` | retrieval | info | Ranking retrieval hits | run folder + retrieval query | verify query text/hash |
| `retrieval.rank.done` | retrieval | info | Ranked retrieval hits | run folder + hits ndjson | inspect hit ordering |
| `retrieval.pack.start` | retrieval | info | Retrieval hit summary | run folder + context pack | inspect path/score/reasons |
| `retrieval.pack.done` | retrieval | info | Built retrieval context pack | run folder + context pack | inspect truncation markers |
| `retrieval.error` | retrieval | error | Retrieval failed | run folder + retrieval artifacts | inspect retrieval error code and message |
| `builder.synthesis.start` | builder | info | Starting plan synthesis | run folder + plan synthesis request | inspect retrievalHash and constraints |
| `builder.synthesis.end` | builder | info | Plan synthesis completed | run folder + synthesized plan | inspect synthesized planHash and steps |
| `builder.synthesis.failed` | builder | error | Plan synthesis failed | run folder + synthesis artifacts | inspect retrieval prerequisites and request hash |
| `error` | startup/plan/provider/env/execute/tool/finalize/replay/retrieval/builder | error | Failure | run folder + failing artifact refs | open `failure-fingerprint.json` then narration tail |
