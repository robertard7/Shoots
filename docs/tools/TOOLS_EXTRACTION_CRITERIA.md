# When to extract `Shoots.Tools` into its own repository

Keep tools inside the Shoots monorepo until all criteria are true:

1. Tool catalog and handlers are stable for at least two consecutive release cycles.
2. Contract tests enforce deterministic catalog and output/error shape rules.
3. Host/runtime/provider layers reference only tools abstractions where applicable.
4. CI includes dedicated tool smoke and contract guard jobs across Linux + Windows runners.
5. Packaging includes catalog and tool assemblies as explicit release artifacts.

When all are true, extract with frozen package boundaries and compatibility tests.
