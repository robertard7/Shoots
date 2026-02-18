# Provider Boundary Inventory

- Existing runtime envelope found: `Shoots.Runtime.Abstractions.ExecutionEnvelope` (`src/Runtime/Shoots.Runtime.Abstractions/ExecutionEnvelope.cs`) representing full runtime snapshot persistence envelope.
- Existing provider decision request found: `Shoots.Runtime.Abstractions.AiDecisionRequest` and callback interface `IAiDecisionProvider`.
- Existing provider bridge interfaces found under providers abstractions: `IAiProviderAdapter`, `IAiProviderCapabilities`, `IAiProviderHealth`.
- No existing `ExecutionResult` type found in solution.
- No existing `IProviderClient` execute-envelope boundary found in solution.
