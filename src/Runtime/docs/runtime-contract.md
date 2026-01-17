Shoots Runtime is sealed at v0.1.x.

Rules:
- No UI frameworks
- No web frameworks
- No NuGet dependencies in Core or Abstractions
- No breaking changes after v1.0
- All extensions must live outside Core
- Errors do not throw across boundaries

## Execution Rule

All command execution MUST pass through RuntimeEngine.
Modules may not dispatch, invoke, or chain commands.
Modules are pure command handlers.

Breaking changes require:
- New major runtime version
- Parallel runtime hosting
- Explicit module opt-in

Existing runtimes are immutable.
