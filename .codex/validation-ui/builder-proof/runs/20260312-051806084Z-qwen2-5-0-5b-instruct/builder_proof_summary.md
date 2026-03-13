# Builder Proof Summary

- Run ID: `20260312-051806084Z-qwen2-5-0-5b-instruct`
- Model: `qwen2.5:0.5b-instruct`
- Provider: `ollama`
- Repo-local classification: `passed_with_recovery`
- Repo-local floor verdict: `sufficient_with_repair_loop`
- External classification: `passed_cleanly`
- External floor verdict: `sufficient_for_bounded_external_targets`
- Clean success count: `6`
- Repaired success count: `1`
- Too-fragile count: `0`

## Repo-Local Targets
- Class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Service sample: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Small test project: recovered_with_guidance (generated_with_bounded_failure; build=failed; test=blocked)

## External Targets
- External class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- External console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- External test-bearing target: passed_cleanly (generated_cleanly; build=passed; test=passed)

## Failure Patterns
- Observed 1 low-floor stumble pattern(s): partial_implementation_gap=1.

## Floor Policy
- The floor model is acceptable for tiny template-driven builds, including the bounded external target pack, when explicit file and namespace hints are present.