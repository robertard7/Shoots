# Builder Model Floor Policy

- Model: `qwen2.5:0.5b-instruct`
- Repo-local verdict: `sufficient_with_repair_loop`
- External verdict: `sufficient_for_bounded_external_targets`
- Summary: The floor model remains acceptable for bounded template-driven builds and small multi-file additions, but bounded refactor-style probes should be routed to a stronger model.

## Guidance
- Good for tiny template-driven console, class-library, service, UI, and external starter targets when explicit file and namespace hints are present.
- Small multi-file additions stay acceptable when the task remains inside the recorded project and file list.
- Keep bounded compile-fix recovery available for isolated compile-fix and test-extension tasks.
- Escalate to a stronger model when a proof target stays too fragile after the bounded repair loop.
- Route bounded_refactor work to a stronger model instead of repeating low-floor attempts.