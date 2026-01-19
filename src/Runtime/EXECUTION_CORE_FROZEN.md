# Execution Core Frozen

The runtime execution core is frozen. Mermaid-derived RouteRules are the sole routing authority.
Tool selection is the only provider responsibility; tool choice never advances the graph.
Provider failures halt routing and are recorded in the routing trace.
