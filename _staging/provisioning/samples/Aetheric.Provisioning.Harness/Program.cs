using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;

if (args.Length > 0) return await DurableHarness.RunAsync(args);

var session = new SimulationSession();
var planning = session.Planner.Plan(session.Input);
if (!planning.IsValid) return 1;
var plan = planning.Plan!;
Console.WriteLine($"SIMULATION — Decisions at {plan.Input.DefinitionSource.Commit}");
Console.WriteLine($"{plan.Steps.Count(s => s.Kind == StepKind.CheckParent)} parent checks; " +
    $"{plan.Steps.Count(s => s.Kind == StepKind.ProvisionOwned)} owned action. No live infrastructure.");
session.Provider.FailNext = true;
var failed = await session.Engine.ExecuteAsync(plan);
var firstCredential = session.Provider.LastCredential;
Console.WriteLine($"First attempt: {(failed.Succeeded ? "unexpected success" : "expected failure")}");
var retried = await session.Engine.ExecuteAsync(plan);
var repeated = await session.Engine.ExecuteAsync(plan);
var passed = !failed.Succeeded && retried.Succeeded && repeated.Succeeded
    && session.Provider.CreatedCount == 1 && session.Provider.Attempts == 2
    && firstCredential == session.Provider.LastCredential;
Console.WriteLine($"Retry: {retried.Succeeded}; repeat: {repeated.Succeeded}; " +
    $"created: {session.Provider.CreatedCount}; credential reused: {firstCredential == session.Provider.LastCredential}");
Console.WriteLine(passed ? "M1 simulation gate passed." : "M1 simulation gate failed.");
return passed ? 0 : 1;
