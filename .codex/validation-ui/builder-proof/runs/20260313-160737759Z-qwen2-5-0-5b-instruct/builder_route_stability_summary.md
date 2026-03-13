# Builder Route Stability Summary

## Route
- Task class: bounded_refactor
- Route: split_first_low_floor_route
- Readiness state: confirmed_for_bounded_use

## Evidence
- Supporting proof runs: 3
- Supporting prepared launches: 3
- Confirmation count: 2
- Contradiction count: 1
- Latest route comparison: confirmed
- Reconfirmation status: reconfirmed_after_contradiction
- Contradiction attribution: override_route_failure
- Fresh proof runs after latest contradiction: 2
- Fresh launch confirmations after latest contradiction: 2
- Reconfirmation proof threshold: 1
- Reconfirmation launch threshold: 1
- Default route suspended: False

## Recommendation
- split_first_low_floor_route is builder-ready for bounded bounded_refactor work.

## Contradictions
- 20260313-140824790Z-qwen2-5-0-5b-instruct: override route direct_low_floor_route was insufficient for scope while default route split_first_low_floor_route remained the confirmed candidate.