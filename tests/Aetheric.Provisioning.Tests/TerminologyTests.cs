using Aetheric.Provisioning.Components.Terminology;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class TerminologyTests
{
    [Fact]
    public void Academic_is_default_and_matches_class_names()
    {
        var t = TerminologyService.Resolve(TerminologySelection.Default);
        Assert.Equal(new Terms("University", "Faculty", "Campus", "Institution", "Department"), t);
    }

    [Fact]
    public void Corporate_template_maps_as_specified()
    {
        var t = TerminologyService.Resolve(new("corporate"));
        Assert.Equal(new Terms("Enterprise", "Corporation", "OfficeLocation", "Division", "Department"), t);
    }

    [Fact]
    public void Unknown_template_falls_back_to_academic()
        => Assert.Equal(TerminologyTemplate.Academic.Terms, TerminologyService.Resolve(new("nope")));

    [Fact]
    public void Custom_terms_are_trimmed_and_blanks_default()
    {
        var t = TerminologyService.Resolve(new(TerminologyTemplate.CustomId, new(" Realm ", "", "Site", "  ", "Team")));
        Assert.Equal(new Terms("Realm", "Faculty", "Site", "Institution", "Team"), t);
    }

    [Theory]
    [InlineData("University", "Universities")]
    [InlineData("Faculty", "Faculties")]
    [InlineData("Campus", "Campuses")]
    [InlineData("Department", "Departments")]
    [InlineData("OfficeLocation", "OfficeLocations")]
    [InlineData("Day", "Days")]
    public void Plurals(string singular, string plural) => Assert.Equal(plural, Terms.Pluralize(singular));

    [Theory]
    [InlineData("University", "a University")]
    [InlineData("Enterprise", "an Enterprise")]
    [InlineData("OfficeLocation", "an OfficeLocation")]
    [InlineData("Division", "a Division")]
    public void Articles(string word, string expected) => Assert.Equal(expected, Terms.WithArticle(word));

    [Fact]
    public async Task Service_persists_and_notifies()
    {
        var store = new InMemoryTerminologyStore();
        var service = new TerminologyService(store);
        var changed = 0;
        service.Changed += () => changed++;
        await service.SaveAsync(new("corporate"));
        Assert.Equal("Enterprise", service.Terms.University);
        Assert.Equal(1, changed);
        var fresh = new TerminologyService(store);
        await fresh.EnsureLoadedAsync();
        Assert.Equal("Enterprise", fresh.Terms.University);
    }

    [Fact]
    public async Task File_store_round_trips_and_survives_corruption()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sub", "terminology.json");
            var store = new FileTerminologyStore(path);
            Assert.Equal(TerminologySelection.Default, await store.LoadAsync());
            var custom = new TerminologySelection(TerminologyTemplate.CustomId, new("A", "B", "C", "D", "E"));
            await store.SaveAsync(custom);
            Assert.Equal(custom.CustomTerms, (await store.LoadAsync()).CustomTerms);
            await File.WriteAllTextAsync(path, "{ not json");
            Assert.Equal(TerminologySelection.Default, await store.LoadAsync());
        }
        finally { dir.Delete(true); }
    }
}
