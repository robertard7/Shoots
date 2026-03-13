# Builder Model Scope Summary

- Model: `qwen2.5:0.5b-instruct`
- Repo-local verdict: `sufficient_with_repair_loop`
- External verdict: `sufficient_for_bounded_external_targets`
- Summary: Clean band=13. Repair-loop band=1. Escalation recommended=0. Reject=1.

## Supported Clean Tasks
- Class library (add_small_function)
- Console app (trivial_edit)
- External class library (add_small_function)
- External console app (trivial_edit)
- External multi-file console app (multi_file_console_feature)
- External related-file library (library_related_files)
- External test-bearing target (fill_missing_implementation_from_strong_hints)
- Multi-file console app (multi_file_console_feature)
- Related-file class library (library_related_files)
- Service feature addition (service_feature_addition)
- Service sample (tiny_sample_app_from_template)
- Test extension target (test_extension)
- WPF feature addition (ui_feature_addition)

## Repair-Assisted Tasks
- Small test project (compile_fix_edit)

## Escalation-Recommended Tasks

## Declined Floor Tasks
- Bounded refactor probe (bounded_refactor)

## Weak Spots
- file_placement_mistake: boundary_of_model_floor
- partial_implementation_gap: acceptable_with_repair_loop