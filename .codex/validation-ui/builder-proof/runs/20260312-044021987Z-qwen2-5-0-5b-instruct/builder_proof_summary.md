# Builder Proof Summary

- Run ID: `20260312-044021987Z-qwen2-5-0-5b-instruct`
- Model: `qwen2.5:0.5b-instruct`
- Provider: `ollama`
- Final classification: `passed_with_recovery`
- Model floor verdict: `sufficient_with_repair_loop`
- Build pass count: `3`
- Test pass count: `0`
- Recovery required count: `1`

## Targets
- Class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Service sample: passed_cleanly (generated_cleanly; build=passed; test=not_applicable)
- Small test project: recovered_with_guidance (generated_with_bounded_failure; build=failed; test=blocked)