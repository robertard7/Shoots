# Builder Proof Summary

- Run ID: `20260313-022141009Z-qwen2-5-0-5b-instruct`
- Model: `qwen2.5:0.5b-instruct`
- Provider: `ollama`
- Repo-local classification: `passed_with_routing`
- Repo-local floor verdict: `sufficient_with_repair_loop`
- External classification: `passed_cleanly`
- External floor verdict: `sufficient_for_bounded_external_targets`
- Clean-band count: `13`
- Repair-loop-band count: `1`
- Escalation-band count: `0`
- Reject-band count: `1`

## Repo-Local Targets
- Bounded refactor probe: failed_after_followup (generated_with_bounded_failure; build=failed; test=not_applicable; files=3; band=reject_band; routing=task_out_of_scope_for_floor)
- Class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=1; band=clean_build_band; routing=proceed_with_current_model)
- Console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=1; band=clean_build_band; routing=proceed_with_current_model)
- Multi-file console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=2; band=clean_build_band; routing=proceed_with_current_model)
- Related-file class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=3; band=clean_build_band; routing=proceed_with_current_model)
- Service feature addition: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=2; band=clean_build_band; routing=proceed_with_current_model)
- Service sample: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=1; band=clean_build_band; routing=proceed_with_current_model)
- Test extension target: passed_cleanly (generated_with_bounded_failure; build=passed; test=passed; files=1; band=clean_build_band; routing=proceed_with_current_model)
- Small test project: recovered_with_guidance (generated_with_bounded_failure; build=failed; test=blocked; files=1; band=repair_loop_band; routing=proceed_with_repair_loop_expected)
- WPF feature addition: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=2; band=clean_build_band; routing=proceed_with_current_model)

## External Targets
- External class library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=1; band=clean_build_band; routing=proceed_with_current_model)
- External console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=1; band=clean_build_band; routing=proceed_with_current_model)
- External multi-file console app: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=2; band=clean_build_band; routing=proceed_with_current_model)
- External related-file library: passed_cleanly (generated_cleanly; build=passed; test=not_applicable; files=3; band=clean_build_band; routing=proceed_with_current_model)
- External test-bearing target: passed_cleanly (generated_cleanly; build=passed; test=passed; files=1; band=clean_build_band; routing=proceed_with_current_model)

## Failure Patterns
- Observed 2 low-floor stumble pattern(s): file_placement_mistake=1, partial_implementation_gap=1.

## Trust Bands
- Clean band=13. Repair-loop band=1. Escalation recommended=0. Reject=1.

## Routing Recommendation
- Bounded refactor probe is out of scope for the low-floor model and should be declined or routed upward.

## Escalation Decision
- Bounded refactor probe should be split into smaller bounded steps before another low-floor attempt, and keep a stronger model ready if the split still shows boundary weak spots. Primary weak spot: File placement mistake appeared 1 time(s) and is currently classified as boundary_of_model_floor.

## Routing Plan
- Bounded refactor probe should be split into smaller bounded steps before another low-floor attempt. The strongest linked weak spot is File placement mistake appeared 1 time(s) and is currently classified as boundary_of_model_floor.

## Floor Policy
- The floor model remains acceptable for bounded template-driven builds and small multi-file additions, but bounded refactor-style probes should be routed to a stronger model.