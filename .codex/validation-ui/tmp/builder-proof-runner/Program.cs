using System.IO;
using System.Net.Http;
using Shoots.UI.Builder;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Services.Backends;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: builder-proof-runner <repo-root>");
    return 1;
}

var repoRoot = Path.GetFullPath(args[0]);
var registry = new ToolRegistry(Path.Combine(repoRoot, "etc", "ui.tools.catalog.json"));
var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
var ollamaHttpClient = new HttpClient
{
    BaseAddress = new Uri(EndpointResolver.ResolveOllamaEndpoint(), UriKind.Absolute)
};
var ollamaClient = new OllamaClient(ollamaHttpClient);
var service = new BuilderExecutionService(
    runtimeBridge,
    new ArtifactManager(),
    registry,
    builderStrongerTierResolver: new OllamaBuilderStrongerTierResolver(ollamaClient, EndpointResolver.ResolveOllamaEndpoint()));

var defaultRun = await service.RunBuilderProofMatrixAsync(
    repoRoot,
    BuilderExecutionService.BuilderProofFloorModelId,
    "ollama");
var defaultComparative = await service.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
var defaultPreparedResult = await service.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

var corroboratingRun = await service.RunBuilderProofMatrixAsync(
    repoRoot,
    BuilderExecutionService.BuilderProofFloorModelId,
    "ollama");
await service.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
var corroboratingPreparedResult = await service.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

var defaultedRun = await service.RunBuilderProofMatrixAsync(
    repoRoot,
    BuilderExecutionService.BuilderProofFloorModelId,
    "ollama");
await service.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
var defaultedPreparedResult = await service.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

var overrideRun = await service.RunBuilderProofMatrixAsync(
    repoRoot,
    BuilderExecutionService.BuilderProofFloorModelId,
    "ollama");
var overrideComparative = await service.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
var overridePreparedResult = await service.LaunchPreparedBuilderRouteAsync(
    repoRoot,
    provider: "ollama",
    routeOverride: "direct_low_floor_route",
    overrideReason: "Proof runner override launch against the confirmed split-first route.");

var reconfirmedRun = await service.RunBuilderProofMatrixAsync(
    repoRoot,
    BuilderExecutionService.BuilderProofFloorModelId,
    "ollama");
await service.RunBuilderComparativeProofAsync(repoRoot, provider: "ollama");
var reconfirmedPreparedResult = await service.LaunchPreparedBuilderRouteAsync(repoRoot, provider: "ollama");

var splitOutcome = BuilderExecutionService.LoadBuilderSplitFirstOutcome(defaultRun.RunFolder)
                   ?? throw new InvalidOperationException("Prepared launch did not record a split-first outcome for the default proof run.");
var defaultLaunchDecision = BuilderExecutionService.LoadBuilderLaunchDefaultDecision(defaultedRun.RunFolder);
var defaultLaunch = BuilderExecutionService.LoadBuilderExecutionLaunch(defaultedRun.RunFolder);
var defaultLaunchResult = BuilderExecutionService.LoadBuilderExecutionResult(defaultedRun.RunFolder);
var externalVerdict = BuilderExecutionService.LoadBuilderExternalFloorVerdict(reconfirmedRun.RunFolder);
var policy = BuilderExecutionService.LoadBuilderModelFloorPolicy(reconfirmedRun.RunFolder);
var strongerTier = BuilderExecutionService.LoadBuilderStrongerTierAvailability(reconfirmedRun.RunFolder);
var routingPolicy = BuilderExecutionService.LoadBuilderRoutingPolicyEvidence(reconfirmedRun.RunFolder);
var splitPlan = BuilderExecutionService.LoadBuilderSplitFirstPlan(reconfirmedRun.RunFolder);
var tieredRouting = BuilderExecutionService.LoadBuilderTieredRoutingPolicy(reconfirmedRun.RunFolder);
var defaultGuidance = BuilderExecutionService.LoadBuilderDefaultPolicy(reconfirmedRun.RunFolder);
var latestRoutingDecision = BuilderExecutionService.LoadBuilderRequestPolicyDecision(reconfirmedRun.RunFolder);
var guidanceSupport = BuilderExecutionService.LoadBuilderPolicyStability(reconfirmedRun.RunFolder);
var intake = BuilderExecutionService.LoadBuilderRequestIntake(reconfirmedRun.RunFolder);
var prep = BuilderExecutionService.LoadBuilderExecutionPrep(reconfirmedRun.RunFolder);
var launch = BuilderExecutionService.LoadBuilderExecutionLaunch(reconfirmedRun.RunFolder);
var launchResult = BuilderExecutionService.LoadBuilderExecutionResult(reconfirmedRun.RunFolder);
var launchDefaultDecision = BuilderExecutionService.LoadBuilderLaunchDefaultDecision(overrideRun.RunFolder);
var overrideEvidence = BuilderExecutionService.LoadBuilderRouteOverrideEvidence(overrideRun.RunFolder);
var routeReview = BuilderExecutionService.LoadBuilderPolicyReviewCandidates(reconfirmedRun.RunFolder);
var overrideReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(overrideRun.RunFolder);
var overrideRecovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(overrideRun.RunFolder);
var readinessGate = BuilderExecutionService.LoadBuilderReadinessGate(reconfirmedRun.RunFolder);
var confirmedClasses = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(reconfirmedRun.RunFolder);
var defaultRouteDecision = BuilderExecutionService.LoadBuilderDefaultRouteDecision(reconfirmedRun.RunFolder);
var readinessContradictions = BuilderExecutionService.LoadBuilderReadinessContradictions(reconfirmedRun.RunFolder);
var reconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(reconfirmedRun.RunFolder);
var recovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(reconfirmedRun.RunFolder);
var splitExecution = BuilderExecutionService.LoadBuilderSplitStepExecution(defaultRun.RunFolder);

Console.WriteLine($"RUN_FOLDER={reconfirmedRun.RunFolder}");
Console.WriteLine($"DEFAULT_RUN_FOLDER={defaultRun.RunFolder}");
Console.WriteLine($"CORROBORATING_RUN_FOLDER={corroboratingRun.RunFolder}");
Console.WriteLine($"DEFAULTED_RUN_FOLDER={defaultedRun.RunFolder}");
Console.WriteLine($"OVERRIDE_RUN_FOLDER={overrideRun.RunFolder}");
Console.WriteLine($"RECONFIRMED_RUN_FOLDER={reconfirmedRun.RunFolder}");
Console.WriteLine($"REPO_LOCAL_VERDICT={reconfirmedRun.ModelFloorVerdict}");
Console.WriteLine($"EXTERNAL_VERDICT={externalVerdict?.Verdict ?? "missing"}");
Console.WriteLine($"SUMMARY={reconfirmedRun.VerdictSummary}");
Console.WriteLine($"GUIDANCE={policy?.Summary ?? "missing"}");
Console.WriteLine($"STRONGER_TIER={strongerTier?.ConfiguredStrongerTierId ?? "missing"}");
Console.WriteLine($"STRONGER_TIER_STATE={strongerTier?.AvailabilityState ?? "missing"}");
Console.WriteLine($"COMPARATIVE_CLASSIFICATION={overrideComparative.ComparativeClassification}");
Console.WriteLine($"ROUTING_POLICY={routingPolicy?.RoutingPolicyState ?? "missing"}");
Console.WriteLine($"SPLIT_PLAN={splitPlan?.SplitRecommendationState ?? "missing"}");
Console.WriteLine($"TIERED_ROUTING={tieredRouting?.PrimaryRoutingState ?? "missing"}");
Console.WriteLine($"DEFAULT_GUIDANCE={(defaultGuidance?.Summary ?? "missing")}");
Console.WriteLine($"REQUEST_GUIDANCE={(latestRoutingDecision?.ChosenPolicyState ?? "missing")}");
Console.WriteLine($"GUIDANCE_SUPPORT={(guidanceSupport?.SupportLevel ?? "missing")}");
Console.WriteLine($"INTAKE_STATE={(intake?.IntakeClassificationState ?? "missing")}");
Console.WriteLine($"PREP_ROUTE={(prep?.SelectedRoute ?? "missing")}");
Console.WriteLine($"LAUNCH_STATE={(launch?.LaunchEligibilityState ?? "missing")}");
Console.WriteLine($"ROUTE_RESULT={(launchResult?.FinalRouteOutcomeClassification ?? reconfirmedPreparedResult.FinalRouteOutcomeClassification)}");
Console.WriteLine($"ROUTE_COMPARISON={(launchResult?.PreparedRouteComparisonState ?? reconfirmedPreparedResult.PreparedRouteComparisonState)}");
Console.WriteLine($"DEFAULT_LAUNCH_STATE={(defaultLaunch?.LaunchEligibilityState ?? "missing")}");
Console.WriteLine($"DEFAULT_ROUTE_RESULT={(defaultLaunchResult?.FinalRouteOutcomeClassification ?? defaultedPreparedResult.FinalRouteOutcomeClassification)}");
Console.WriteLine($"DEFAULT_ROUTE_COMPARISON={(defaultLaunchResult?.PreparedRouteComparisonState ?? defaultedPreparedResult.PreparedRouteComparisonState)}");
Console.WriteLine($"CORROBORATING_ROUTE_RESULT={corroboratingPreparedResult.FinalRouteOutcomeClassification}");
Console.WriteLine($"RECONFIRMED_ROUTE_RESULT={reconfirmedPreparedResult.FinalRouteOutcomeClassification}");
Console.WriteLine($"DEFAULT_ROUTE_SOURCE={(defaultLaunchDecision?.RouteSourceState ?? "missing")}");
Console.WriteLine($"DEFAULT_OPERATOR_STATE={(defaultLaunchDecision?.OperatorDecisionState ?? "missing")}");
Console.WriteLine($"OVERRIDE_ROUTE_SOURCE={(launchDefaultDecision?.RouteSourceState ?? "missing")}");
Console.WriteLine($"OVERRIDE_OPERATOR_STATE={(launchDefaultDecision?.OperatorDecisionState ?? "missing")}");
Console.WriteLine($"OVERRIDE_COMPARISON={(overrideEvidence?.OverrideOutcomeComparisonState ?? "missing")}");
Console.WriteLine($"OVERRIDE_RECONFIRMATION={(overrideReconfirmation?.CurrentReconfirmationState ?? "missing")}");
Console.WriteLine($"OVERRIDE_RECOVERY={(overrideRecovery?.RecoveryState ?? "missing")}");
Console.WriteLine($"OVERRIDE_REVIEW={(routeReview?.Summary ?? "missing")}");
Console.WriteLine($"READINESS_GATE={(readinessGate?.CurrentReadinessGateState ?? "missing")}");
Console.WriteLine($"READINESS_CONFIRMATIONS={(readinessGate?.ConfirmationCount.ToString() ?? "missing")}");
Console.WriteLine($"READINESS_CONTRADICTIONS={(readinessGate?.ContradictionCount.ToString() ?? "missing")}");
Console.WriteLine($"CONFIRMED_CLASSES={(confirmedClasses?.Summary ?? "missing")}");
Console.WriteLine($"CURRENT_DEFAULT_ROUTE_SOURCE={(defaultRouteDecision?.RouteSourceState ?? "missing")}");
Console.WriteLine($"DEFAULT_ROUTE_SUSPENDED={(defaultRouteDecision?.DefaultRouteSuspended.ToString() ?? "missing")}");
Console.WriteLine($"ROUTE_CONTRADICTIONS={(readinessContradictions?.Summary ?? "missing")}");
Console.WriteLine($"ROUTE_RECONFIRMATION={(reconfirmation?.CurrentReconfirmationState ?? "missing")}");
Console.WriteLine($"ROUTE_RECOVERY={(recovery?.RecoveryState ?? "missing")}");
Console.WriteLine($"SPLIT_EXECUTION={splitExecution?.FreshnessState ?? "missing"}");
Console.WriteLine($"SPLIT_CLOSURE={splitOutcome.ClosureClassification}");

return 0;
