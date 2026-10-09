namespace Aetheric.Provisioning.Components.Terminology;

/// <summary>
/// The terms the UI should show right now. Inject this and read <see cref="Terms"/>; never hard-code
/// University/Faculty/Campus/Institution/Department in markup.
/// </summary>
public sealed class TerminologyService(ITerminologyStore store)
{
    private bool _loaded;

    public TerminologySelection Selection { get; private set; } = TerminologySelection.Default;
    public Terms Terms { get; private set; } = TerminologyTemplate.Academic.Terms;
    public event Action? Changed;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        Apply(await store.LoadAsync(ct));
        _loaded = true;
    }

    public async Task SaveAsync(TerminologySelection selection, CancellationToken ct = default)
    {
        await store.SaveAsync(selection, ct);
        Apply(selection);
        _loaded = true;
        Changed?.Invoke();
    }

    private void Apply(TerminologySelection selection)
    {
        Selection = selection;
        Terms = Resolve(selection);
    }

    public static Terms Resolve(TerminologySelection selection)
    {
        if (selection.TemplateId == TerminologyTemplate.CustomId && selection.CustomTerms is { } custom)
            return custom.Normalized();
        return TerminologyTemplate.BuiltIn.FirstOrDefault(t => t.Id == selection.TemplateId)?.Terms
               ?? TerminologyTemplate.Academic.Terms;
    }
}
