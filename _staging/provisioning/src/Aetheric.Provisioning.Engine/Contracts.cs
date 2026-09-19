using System.Collections.Immutable;

namespace Aetheric.Provisioning.Engine;

// These are engine inputs, not a replacement for the runtime's institutional schema.
public sealed record SourceProvenance(string Repository, string Commit, string Path, string ContentHash);
public sealed record ResourceRequirement(string Id, string Name, string Category, string Ownership);
public sealed record InstitutionRequirements(string Id, string Version,
    ImmutableArray<ResourceRequirement> Resources, ImmutableArray<string> ParentContracts);
public sealed record SecretReference(string Id);
public sealed record ResourceBinding(string Provider, ImmutableDictionary<string, string> Settings,
    ImmutableDictionary<string, SecretReference> Secrets);
public sealed record DeploymentBindings(string InstitutionId, string InstitutionVersion, string Environment,
    ImmutableDictionary<string, ResourceBinding> Resources,
    ImmutableDictionary<string, string> ParentSources);
public sealed record ParentContext(string Id, string Revision)
{
    // Explicit capability catalog supplied by the host; live verification is the resolver's responsibility.
    public ImmutableDictionary<string, string> Capabilities { get; init; } = ImmutableDictionary<string, string>.Empty;
}
public sealed record PlanningInput(InstitutionRequirements Institution, DeploymentBindings Bindings,
    ParentContext Parent, SourceProvenance DefinitionSource, SourceProvenance BindingsSource);
public sealed record ValidationIssue(string Code, string Target, string Message);
public enum StepKind { CheckParent, ProvisionOwned }
public enum OutcomeStatus { Succeeded, AlreadySatisfied, Failed, Blocked, Cancelled }
public sealed record PlanStep(string Id, StepKind Kind, string Target, string? Source,
    ResourceRequirement? Resource, ResourceBinding? Binding, ImmutableArray<string> DependsOn);

// Only the planner can construct an executable plan. All contents are immutable snapshots.
public sealed class ProvisioningPlan
{
    internal ProvisioningPlan(string id, PlanningInput input, ImmutableArray<PlanStep> steps)
        => (Id, Input, Steps) = (id, input, steps);
    public string Id { get; }
    public PlanningInput Input { get; }
    public ImmutableArray<PlanStep> Steps { get; }
}
public sealed record PlanningResult(ProvisioningPlan? Plan, ImmutableArray<ValidationIssue> Issues)
{
    public bool IsValid => Plan is not null && Issues.IsEmpty;
}
public sealed record StepOutcome(string StepId, OutcomeStatus Status, string Code,
    ImmutableArray<SecretReference> Secrets)
{
    public bool IsSuccessful => Status is OutcomeStatus.Succeeded or OutcomeStatus.AlreadySatisfied;
}
public sealed record RunState(string PlanId, ImmutableDictionary<string, StepOutcome> Outcomes);
public sealed record RunResult(string PlanId, ImmutableArray<StepOutcome> Outcomes)
{
    public bool Succeeded => Outcomes.All(x => x.IsSuccessful);
}
public sealed record ProvisioningProgress(string PlanId, StepOutcome Outcome);
public sealed record ProviderResult(bool AlreadyExists, ImmutableArray<SecretReference> Secrets);
public sealed record ProviderContext(string PlanId, string Environment, string InstitutionId,
    ResourceRequirement Resource, ResourceBinding Binding, ISecretStore Secrets);

public interface IResourceProvider
{
    string Key { get; }
    // Validation must be pure: no provisioning, network calls, or secret generation.
    ImmutableArray<ValidationIssue> Validate(ResourceRequirement resource, ResourceBinding binding);
    // Implementations must be idempotent and must never overwrite incompatible existing resources.
    Task<ProviderResult> EnsureAsync(ProviderContext context, CancellationToken cancellationToken);
}
public interface IParentCapabilityResolver
{
    Task<bool> IsAvailableAsync(ParentContext parent, string contract, string source,
        CancellationToken cancellationToken);
}
public interface IRunStateStore
{
    Task<RunState?> ReadAsync(string planId, CancellationToken cancellationToken);
    Task SaveAsync(RunState state, CancellationToken cancellationToken);
}
public interface ISecretStore
{
    // Implementations atomically create or reuse a secret and return an opaque reference.
    Task<SecretReference> GetOrCreateAsync(string scope, string name, CancellationToken cancellationToken);
    Task<string> ReadAsync(SecretReference reference, CancellationToken cancellationToken);
}

// Root-level admin access to existing infrastructure (Redis, Mongo, RabbitMQ, Postgres, ...) the
// provisioner authenticates with in order to create child resources. Distinct from ISecretStore,
// which generates and never replaces secrets for resources the engine itself creates - a root
// credential is supplied by the operator for infrastructure that already exists, and must support
// an explicit, deliberate overwrite (correcting a typo, rotating a password).
public sealed record MongoRootOptions(string AuthDatabase = "admin", bool DirectConnection = false);
public sealed record PostgresRootOptions(string Database = "postgres");
public sealed record RabbitMqRootOptions(string Scheme = "http", string BasePath = "/");
public sealed record RootCredential(string Host, int Port, string? Username, string Password)
{
    public MongoRootOptions? Mongo { get; init; }
    public PostgresRootOptions? Postgres { get; init; }
    public RabbitMqRootOptions? RabbitMq { get; init; }
    public override string ToString() => "RootCredential { Password = [redacted] }";
}
public interface IRootCredentialStore
{
    // Always overwrites; this is not get-or-create.
    Task SetAsync(string system, RootCredential credential, CancellationToken cancellationToken);
    Task<RootCredential?> TryReadAsync(string system, CancellationToken cancellationToken);
}

// A shared store can serialize a complete read/execute/checkpoint cycle across engines/processes.
public interface IRunExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(string planId, CancellationToken cancellationToken);
}
