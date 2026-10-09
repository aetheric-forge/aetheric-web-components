namespace Aetheric.Provisioning.Components.Terminology;

/// <summary>
/// The display names for the five organisational levels. Keys are the object-class names
/// (University, Faculty, Campus, Institution, Department); values are what the UI calls them.
/// Display only: contracts, requests and stored data keep the class names.
/// </summary>
public sealed record Terms(string University, string Faculty, string Campus, string Institution, string Department)
{
    public string UniversityPlural => Pluralize(University);
    public string FacultyPlural => Pluralize(Faculty);
    public string CampusPlural => Pluralize(Campus);
    public string InstitutionPlural => Pluralize(Institution);
    public string DepartmentPlural => Pluralize(Department);

    /// <summary>Returns the terms with blanks replaced by the academic defaults and surrounding whitespace removed.</summary>
    public Terms Normalized() => new(
        Pick(University, TerminologyTemplate.Academic.Terms.University),
        Pick(Faculty, TerminologyTemplate.Academic.Terms.Faculty),
        Pick(Campus, TerminologyTemplate.Academic.Terms.Campus),
        Pick(Institution, TerminologyTemplate.Academic.Terms.Institution),
        Pick(Department, TerminologyTemplate.Academic.Terms.Department));

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>"a University", "an Enterprise" - for sentences that introduce a term.</summary>
    public static string WithArticle(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        var w = word.ToLowerInvariant();
        var vowel = "aeiou".Contains(w[0]) && !w.StartsWith("uni") && !w.StartsWith("use") && !w.StartsWith("eu");
        return (vowel ? "an " : "a ") + word;
    }

    // Deliberately small: covers -y, -s/-x/-ch/-sh and the regular case. Terms are short nouns.
    public static string Pluralize(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        if (word.EndsWith('y') && word.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(word[^2])))
            return word[..^1] + "ies";
        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) || word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            return word + "es";
        return word + "s";
    }
}

public sealed record TerminologyTemplate(string Id, string Name, Terms Terms)
{
    public const string CustomId = "custom";

    /// <summary>Default. Matches the object-class names.</summary>
    public static TerminologyTemplate Academic { get; } =
        new("academic", "Academic", new("University", "Faculty", "Campus", "Institution", "Department"));

    public static TerminologyTemplate Corporate { get; } =
        new("corporate", "Corporate", new("Enterprise", "Corporation", "OfficeLocation", "Division", "Department"));

    public static IReadOnlyList<TerminologyTemplate> BuiltIn { get; } = [Academic, Corporate];
}
