# Routing Plumbing Freeze

Routing invariants (locked):

- WorkOrder is immutable and must be reissued to change intent.
- RoutingState is created only via RoutingState.CreateInitial.
- RouteGate is the single authority for state transitions.
- RouteGate never infers intent or retries.
- SelectTool decisions are the only accepted decision outputs.
- ToolInvocation is only allowed on SelectTool and must match WorkOrderId.
- Tool registry validation uses immutable snapshots.
- Contracts.Core routing and tool types are frozen.
- ToolResult is serialized and hashed but not consumed by routing.

Any change to these rules requires explicit approval.

Checklist:

- [x] RouteGate is the only routing state machine
- [x] RoutingState cannot be advanced outside RouteGate
- [x] SelectTool decisions bounded to AI ownership
- [x] ToolInvocation and ToolResult are immutable envelopes only
- [x] Builder avoids ToolRegistry and RoutingState
