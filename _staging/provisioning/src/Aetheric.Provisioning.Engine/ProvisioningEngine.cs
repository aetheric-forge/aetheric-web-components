using System.Collections.Immutable;

namespace Aetheric.Provisioning.Engine;

public sealed class ProvisioningEngine(IEnumerable<IResourceProvider> providers,
    IParentCapabilityResolver parents, IRunStateStore state, ISecretStore secrets)
{
    private readonly ImmutableDictionary<string, IResourceProvider> _providers =
        providers.ToImmutableDictionary(x => x.Key, StringComparer.Ordinal);
    // Stores may additionally serialize the complete run across engine instances/processes.
    private readonly SemaphoreSlim _execution = new(1, 1);

    public async Task<RunResult> ExecuteAsync(ProvisioningPlan plan,
        IProgress<ProvisioningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _execution.WaitAsync(cancellationToken);
        try
        {
            await using var lease = state is IRunExecutionLock locking
                ? await locking.AcquireAsync(plan.Id, cancellationToken) : null;
            var prior = await state.ReadAsync(plan.Id, cancellationToken);
            var outcomes = ImmutableDictionary.CreateBuilder<string, StepOutcome>();
            foreach (var step in plan.Steps)
            {
                StepOutcome outcome;
                if (cancellationToken.IsCancellationRequested)
                    outcome = new(step.Id, OutcomeStatus.Cancelled, "run.cancelled", []);
                else if (step.DependsOn.Any(id => !outcomes.TryGetValue(id, out var dependency) || !dependency.IsSuccessful))
                    outcome = new(step.Id, OutcomeStatus.Blocked, "dependency.failed", []);
                else if (step.Kind == StepKind.ProvisionOwned && prior?.Outcomes.TryGetValue(step.Id, out var done) == true
                    && done.IsSuccessful)
                {
                    // A checkpoint is not usable if its credentials have been lost or cannot be decrypted.
                    foreach (var reference in done.Secrets)
                        _ = await secrets.ReadAsync(reference, cancellationToken);
                    outcome = done with { Status = OutcomeStatus.AlreadySatisfied, Code = "state.completed" };
                }
                else
                    outcome = await ExecuteStepAsync(plan, step, cancellationToken);
                outcomes[step.Id] = outcome;
                // Preserve previously completed work even if a parent is unavailable on this attempt.
                var checkpoint = (prior?.Outcomes ?? ImmutableDictionary<string, StepOutcome>.Empty).ToBuilder();
                foreach (var item in outcomes)
                    if (!checkpoint.TryGetValue(item.Key, out var previous) || !previous.IsSuccessful || item.Value.IsSuccessful)
                        checkpoint[item.Key] = item.Value;
                await state.SaveAsync(new(plan.Id, checkpoint.ToImmutable()), CancellationToken.None);
                progress?.Report(new(plan.Id, outcome));
            }
            return new(plan.Id, plan.Steps.Select(x => outcomes[x.Id]).ToImmutableArray());
        }
        finally { _execution.Release(); }
    }

    private async Task<StepOutcome> ExecuteStepAsync(ProvisioningPlan plan, PlanStep step, CancellationToken ct)
    {
        try
        {
            if (step.Kind == StepKind.CheckParent)
            {
                var available = await parents.IsAvailableAsync(plan.Input.Parent, step.Target, step.Source!, ct);
                return new(step.Id, available ? OutcomeStatus.Succeeded : OutcomeStatus.Failed,
                    available ? "parent.available" : "parent.unavailable", []);
            }
            if (!_providers.TryGetValue(step.Binding!.Provider, out var provider))
                return new(step.Id, OutcomeStatus.Failed, "provider.unsupported", []);
            var validation = provider.Validate(step.Resource!, step.Binding);
            if (!validation.IsEmpty)
                return new(step.Id, OutcomeStatus.Failed, "provider.invalid_binding", []);
            var result = await provider.EnsureAsync(new(plan.Id, plan.Input.Bindings.Environment,
                plan.Input.Institution.Id, step.Resource!, step.Binding, secrets), ct);
            return new(step.Id, result.AlreadyExists ? OutcomeStatus.AlreadySatisfied : OutcomeStatus.Succeeded,
                result.AlreadyExists ? "resource.exists" : "resource.created", result.Secrets);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(step.Id, OutcomeStatus.Cancelled, "run.cancelled", []);
        }
        catch (Exception)
        {
            // Provider exceptions may contain connection strings or secret values. Public outcomes use codes only.
            return new(step.Id, OutcomeStatus.Failed, "operation.failed", []);
        }
    }
}
