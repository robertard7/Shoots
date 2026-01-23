# Universal AI Layer (Shoots Runtime)

The universal AI layer makes AI help a shared runtime service instead of a per-screen feature. Every surface provides its own context, capabilities, and constraints so the AI layer reflects the current state without guessing.

## What ships in this layer

- **IAiHelpSurface**: a small contract for describing context, capabilities, and constraints per screen or asset.
- **AiIntentDescriptor**: intent metadata applied to every AI help call (Explain, Diagnose, Suggest, Modify) along with a target scope.
- **AiHelpFacade**: combines runtime snapshots with all registered surfaces, and stays descriptive and read-only.
- **AiHelpScope**: pins each request to a surface scope and shared data payload.

## Why it exists

The AI layer is infrastructure. It summarizes reality rather than inventing it:

- Context flows from the active workspace, plan, and environment.
- Each surface declares its own boundaries.
- The facade returns descriptive summaries only.

## How surfaces are composed

Surfaces are registered by UI views and runtime components. The facade merges them into a single narrative:

1. A plan or screen creates its surface(s).
2. The UI sends surfaces along with a concrete intent descriptor.
3. The facade returns context, state, and next-step suggestions without executing anything.

## Flow diagram

```
Surface → Intent → AI Help → No Authority
```

## Extending the layer

When adding a new screen or asset type:

- Create a new `IAiHelpSurface` implementation.
- Add the surface to the list collected in the UI or runtime layer.
- Provide an intent descriptor for each AI help request.
