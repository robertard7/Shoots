# Builder Route Stability Summary

## Route
- Task class: bounded_refactor
- Route: split_first_low_floor_route
- Readiness state: unstable_needs_more_evidence

## Evidence
- Supporting proof runs: 3
- Supporting prepared launches: 3
- Confirmation count: 2
- Contradiction count: 1
- Latest route comparison: insufficient_for_scope
- Reconfirmation status: waiting_for_fresh_launch_confirmation
- Contradiction attribution: override_route_failure
- Fresh proof runs after latest contradiction: 0
- Fresh launch confirmations after latest contradiction: 0
- Reconfirmation proof threshold: 1
- Reconfirmation launch threshold: 1
- Default route suspended: True

## Recommendation
- split_first_low_floor_route should stay in bounded use only while more confirmation evidence is gathered.

## Contradictions
- 20260313-161300700Z-qwen2-5-0-5b-instruct: override route direct_low_floor_route was insufficient for scope while default route split_first_low_floor_route remained the confirmed candidate.