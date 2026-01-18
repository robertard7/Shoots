# Routing Plumbing Freeze

Routing invariants (locked):

- WorkOrder is immutable and must be reissued to change intent.
- RoutingState is created only via RoutingState.CreateInitial.
- RouteGate is the single authority for state transitions.
- RouteGate never infers intent or retries.
- SelectTool decisions are the only accepted decision outputs.
- ToolInvocation is only allowed on SelectTool and must match WorkOrderId.
- Tool registry validation uses immutable snapshots.

Any change to these rules requires explicit approval.
