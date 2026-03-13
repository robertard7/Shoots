# Builder Comparative Proof Summary

- Source proof run: `20260313-160946630Z-qwen2-5-0-5b-instruct`
- Task class: `bounded_refactor`
- Target: `Bounded refactor probe`
- Current model: `qwen2.5:0.5b-instruct`
- Stronger-tier model: `qwen2.5:7b-instruct`
- Comparative classification: `cleaner_success`
- Split evidence: `splitting_makes_low_floor_viable`
- Repair burden: Low-floor burden=too_fragile (failed_after_followup). Stronger-tier burden=clean (passed_cleanly). Split low-floor burden=clean (passed_cleanly).

## Outcomes
- Low-floor source: failed_after_followup (Bounded refactor probe: generated_with_bounded_failure. Build=failed. Final=failed_after_followup. Repeated failure classification=beyond_model_floor.)
- Stronger-tier: passed_cleanly (Bounded refactor probe: generated_cleanly. Build=passed. Final=passed_cleanly.)
- Split low-floor: passed_cleanly (Bounded refactor probe (split scope): generated_cleanly. Build=passed. Final=passed_cleanly.)

## Weak-Spot Comparison
- file_placement_mistake: file_placement_mistake was observed on the low-floor path and was not reproduced on the stronger-tier proof.

## Summary
- Low-floor Bounded refactor probe ended as failed_after_followup. Stronger-tier comparison ended as passed_cleanly. Low-floor burden=too_fragile (failed_after_followup). Stronger-tier burden=clean (passed_cleanly). Split low-floor burden=clean (passed_cleanly). Splitting the scope made the floor model viable: Bounded refactor probe (split scope) finished as passed_cleanly, while the original Bounded refactor probe stayed failed_after_followup. Escalation buys a cleaner success for the original bounded scope.