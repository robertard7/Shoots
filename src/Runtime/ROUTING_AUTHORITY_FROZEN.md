# Routing Authority Frozen

Mermaid graph definitions compile into RouteRules that fully govern routing advancement.
RouteGate is the sole authority for node transitions; provider/tool output cannot select the next node.
Routing determinism is enforced via graph hashes and intent tokens.

## Configuration Boundaries

The runtime core does not read configuration during routing. Configuration may only affect
non-authoritative concerns such as logging, provider enablement, and timeouts outside the
routing loop. No configuration value can alter node choice, advancement, or intent tokens.
