# Builder Model Floor Policy

- Model: `qwen2.5:0.5b-instruct`
- Repo-local verdict: `sufficient_with_repair_loop`
- External verdict: `sufficient_for_bounded_external_targets`
- Summary: The floor model is acceptable for tiny template-driven builds, including the bounded external target pack, when explicit file and namespace hints are present.

## Guidance
- Good for tiny template-driven console, class-library, and test-bearing targets when explicit file and namespace hints are present.
- Keep bounded compile-fix recovery available for isolated compile-fix tasks.