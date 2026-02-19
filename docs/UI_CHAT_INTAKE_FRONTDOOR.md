# UI Chat Intake Front Door

## Recon Snapshot

- UI project path: `ui/Shoots.Ui/Shoots.Ui.csproj`.
- UI tests path: `ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj`.
- Current entrypoint surface: `ui/Shoots.Ui/MainWindow.xaml` with `Chat Intake` as the first tab.
- Runtime trigger from UI host: `MainWindowViewModel.StartAsync()` calls `IExecutionCommandService.StartAsync(Plan, cancellationToken)`.

## Front Door Lifecycle (Host-Driven)

1. Draft intake fields are edited in Chat Intake.
2. `Lock WorkOrder` computes deterministic job-spec digest and freezes editable fields.
3. `Generate Plan` creates a deterministic intake plan preview and displays `PlanId` + `PlanHash`.
4. `Run` executes via host command service.
5. If runtime returns `WAITING`, the UI displays waiting details and remains idle.
6. Resume happens only through explicit user action (`Resume (Inject Decision)`), never auto-rerun.

## Waiting + Resume

- Waiting panel surfaces RouteGateId, CurrentNodeId, policy, fallback flag, allowed next nodes, WorkOrderId, and PlanHash.
- User provides decision tool id and JSON bindings; UI computes deterministic `InjectedDecisionDigest` from canonical JSON.
- Resume runs only when WAITING and digest is present; no automatic rerun occurs.
