# AI Guardrails (Shoots Runtime)

The AI help layer is descriptive and read-only. It surfaces reality and constraints without taking actions.

## Guardrails in practice

- AI help never executes tools or triggers runtime actions.
- Every request includes an explicit intent type and target scope.
- Each help surface describes its own constraints so AI guidance stays bounded.

## Intent types

- **Explain**: describe the current context.
- **Diagnose**: identify why a state is blocked or unclear.
- **Suggest**: offer next-step ideas within existing constraints.
- **Modify**: reserved for future workflows that explicitly allow edits.

## Where guardrails live

- UI surfaces register their own descriptions and constraints.
- The facade collects surfaces and refuses to answer if no surface is registered.
- Execution remains controlled by runtime services, not the AI layer.
