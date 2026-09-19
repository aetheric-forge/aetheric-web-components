using System.Collections.Immutable;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Definitions;

public sealed record DefinitionSourceRequest(string Repository, string Revision, string DefinitionPath, string BindingsPath);
public sealed record SourceDocument(string Text, SourceProvenance Provenance);
public sealed record SourceBundle(SourceDocument Definition, SourceDocument Bindings);
public sealed record SourceLoadResult(SourceBundle? Bundle, ImmutableArray<ValidationIssue> Issues);
public interface IDefinitionSource
{
    Task<SourceLoadResult> LoadAsync(DefinitionSourceRequest request, CancellationToken cancellationToken = default);
}
public sealed record LoadedInstitution(string Name, string Description, InstitutionRequirements Requirements,
    DeploymentBindings Bindings, SourceBundle Source);
public sealed record DefinitionReadResult(LoadedInstitution? Institution, ImmutableArray<ValidationIssue> Issues);
