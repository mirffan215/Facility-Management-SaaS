using System.Text.Json;
using Fms.Facilities.Domain;

namespace Fms.UnitTests;

public sealed class FacilitiesDomainTests
{
    [Theory]
    [InlineData(LocationType.Site, null, true)]
    [InlineData(LocationType.Building, LocationType.Site, true)]
    [InlineData(LocationType.Floor, LocationType.Building, true)]
    [InlineData(LocationType.Space, LocationType.Floor, true)]
    [InlineData(LocationType.Space, LocationType.Building, true)]
    [InlineData(LocationType.Site, LocationType.Site, false)]
    [InlineData(LocationType.Building, null, false)]
    [InlineData(LocationType.Floor, LocationType.Site, false)]
    [InlineData(LocationType.Space, LocationType.Space, false)]
    public void Hierarchy_rules(LocationType child, LocationType? parent, bool allowed) =>
        Assert.Equal(allowed, LocationRules.CanBeChildOf(child, parent));

    [Theory]
    [InlineData("RUH-HQ.B1_F03", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("slash/ed", false)]
    [InlineData("عربي", false)]
    public void Codes_are_ascii_identifiers(string code, bool valid) => Assert.Equal(valid, LocationRules.IsValidCode(code));

    [Fact]
    public void Code_length_is_capped() => Assert.False(LocationRules.IsValidCode(new string('a', 51)));

    [Theory]
    [InlineData("fire_rating", true)]
    [InlineData("a1", true)]
    [InlineData("Fire", false)]
    [InlineData("1abc", false)]
    [InlineData("has-dash", false)]
    [InlineData("", false)]
    public void Custom_field_keys(string key, bool valid) => Assert.Equal(valid, CustomFieldValidator.IsValidKey(key));

    private static CustomFieldDefinition Def(string key, CustomFieldType type, bool required = false, params string[] choices) =>
        new() { Key = key, DataType = type, IsRequired = required, ChoicesJson = JsonSerializer.Serialize(choices) };

    private static Dictionary<string, JsonElement> Values(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Each_data_type_accepts_valid_and_rejects_invalid_values()
    {
        var defs = new[]
        {
            Def("t", CustomFieldType.Text), Def("n", CustomFieldType.Number), Def("d", CustomFieldType.Date),
            Def("b", CustomFieldType.Boolean), Def("c", CustomFieldType.Choice, false, "x", "y"),
        };

        var ok = CustomFieldValidator.Validate(defs, [], Values("""{"t":"hi","n":3.5,"d":"2026-10-25","b":true,"c":"x"}"""), "{}");
        Assert.True(ok.IsOk);

        var bad = CustomFieldValidator.Validate(defs, [], Values("""{"t":5,"n":"3","d":"25/10/2026","b":"yes","c":"z"}"""), "{}");
        Assert.Equal(["customFields.b", "customFields.c", "customFields.d", "customFields.n", "customFields.t"], bad.Errors!.Keys.Order());
    }

    [Fact]
    public void Text_length_is_limited_and_required_fields_must_be_present()
    {
        var defs = new[] { Def("t", CustomFieldType.Text, required: true) };

        Assert.False(CustomFieldValidator.Validate(defs, [], Values("{}"), "{}").IsOk);
        Assert.False(CustomFieldValidator.Validate(defs, [], Values("""{"t":null}"""), "{}").IsOk);
        Assert.False(CustomFieldValidator.Validate(defs, [], Values($$"""{"t":"{{new string('x', 1001)}}"}"""), "{}").IsOk);
    }

    [Fact]
    public void Unknown_keys_are_rejected_but_archived_values_are_preserved()
    {
        var defs = new[] { Def("live", CustomFieldType.Text) };

        Assert.False(CustomFieldValidator.Validate(defs, ["old"], Values("""{"old":"edit"}"""), "{}").IsOk);

        var kept = CustomFieldValidator.Validate(defs, ["old"], Values("""{"live":"v"}"""), """{"old":"keep me","live":"stale"}""");
        Assert.True(kept.IsOk);
        Assert.Equal("""{"live":"v","old":"keep me"}""", kept.Value);
    }
}
