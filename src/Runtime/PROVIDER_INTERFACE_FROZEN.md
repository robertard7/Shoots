# Provider Interface Frozen

Providers are tool selectors only. Providers must not route, mutate graphs, or retain state.
No retries, no routing hints, no node IDs, and no graph mutation are allowed.
The provider interface is frozen to prevent authority creep.
