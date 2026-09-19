using System.Collections.Immutable;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Application;

public sealed record ReviewExecutionResult(RunResult? Run, ImmutableArray<ValidationIssue> Issues);

/// <summary>Host-independent review lifecycle. Configure, review, approve the exact plan, then execute.</summary>
public sealed class ProvisioningReview(IDefinitionSource source, InstitutionYamlReader reader,
    ProvisioningPlanner planner, ProvisioningEngine engine)
{
    private readonly object _gate = new();
    private long _generation;
    private string? _approvedId;
    private bool _executing;
    public LoadedInstitution? Loaded { get; private set; }
    public DeploymentBindings? Bindings { get; private set; }
    public ParentContext Parent { get; private set; } = new("", "");
    public ProvisioningPlan? Plan { get; private set; }
    public ImmutableArray<ValidationIssue> Issues { get; private set; } = [];
    public bool IsApproved => Plan is not null && Plan.Id == _approvedId;

    public void ClearSource()
    {
        lock (_gate)
        {
            EnsureEditable();
            _generation++;
            Loaded = null;
            Bindings = null;
            Parent = new("", "");
            InvalidateCore();
        }
    }
    public void InvalidateReview()
    {
        lock (_gate) { EnsureEditable(); InvalidateCore(); }
    }
    public async Task LoadAsync(DefinitionSourceRequest request, CancellationToken ct = default)
    {
        long generation;
        lock (_gate) { ClearSource(); generation = _generation; }
        var result = await source.LoadAsync(request, ct);
        lock (_gate)
        {
            if (generation != _generation) return; // A superseded load cannot restore stale inputs or review.
            if (result.Bundle is null) { Issues = result.Issues; return; }
            LoadCore(result.Bundle);
        }
    }
    public void LoadBundle(SourceBundle bundle)
    {
        lock (_gate) { ClearSource(); LoadCore(bundle); }
    }
    private void LoadCore(SourceBundle bundle)
    {
        var result = reader.Read(bundle);
        Issues = result.Issues;
        Loaded = result.Institution;
        Bindings = Loaded?.Bindings;
    }
    public void Configure(DeploymentBindings bindings, ParentContext parent)
    {
        lock (_gate)
        {
            EnsureEditable();
            if (Loaded is null) throw new InvalidOperationException("Load an institution before configuring it.");
            InvalidateCore();
            Bindings = bindings;
            Parent = parent;
        }
    }
    public PlanningResult Review()
    {
        lock (_gate)
        {
            EnsureEditable();
            InvalidateCore();
            if (Loaded is null || Bindings is null)
                return Failed("review.source_missing", "source", "Load a valid institution and bindings first.");
            var input = new PlanningInput(Loaded.Requirements, Bindings, Parent,
                Loaded.Source.Definition.Provenance, Loaded.Source.Bindings.Provenance);
            var result = planner.Plan(input);
            if (!result.IsValid) { Issues = result.Issues; return result; }
            var unresolved = Loaded.Requirements.ParentContracts.Where(contract =>
                !Parent.Capabilities.TryGetValue(contract, out var available)
                || available != Bindings.ParentSources.GetValueOrDefault(contract))
                .Select(contract => new ValidationIssue("parent.unresolved", contract,
                    "The supplied parent context does not provide this contract at the selected source."))
                .ToImmutableArray();
            if (!unresolved.IsEmpty) { Issues = unresolved; return new(null, Issues); }
            Plan = result.Plan;
            return result;
        }
    }
    public void RevokeApproval()
    {
        lock (_gate) { EnsureEditable(); _approvedId = null; }
    }
    public bool Approve(string planId)
    {
        lock (_gate)
        {
            EnsureEditable();
            if (Plan is null || planId != Plan.Id) return false;
            _approvedId = planId;
            return true;
        }
    }
    public async Task<ReviewExecutionResult> ExecuteApprovedAsync(IProgress<ProvisioningProgress>? progress = null, CancellationToken ct = default)
    {
        ProvisioningPlan plan;
        lock (_gate)
        {
            if (_executing) return new(null, [new("review.busy", "review", "Execution is already running.")]);
            if (Plan is null || !IsApproved) return new(null, [new("review.approval_required", "review", "Review and approve the current plan before execution.")]);
            plan = Plan;
            _executing = true;
        }
        try { return new(await engine.ExecuteAsync(plan, progress, ct), []); }
        finally { lock (_gate) _executing = false; }
    }
    private PlanningResult Failed(string code, string target, string message)
    {
        Issues = [new(code, target, message)];
        return new(null, Issues);
    }
    private void InvalidateCore() { Plan = null; _approvedId = null; Issues = []; }
    private void EnsureEditable()
    {
        if (_executing) throw new InvalidOperationException("Wait for execution to finish before changing inputs.");
    }
}
