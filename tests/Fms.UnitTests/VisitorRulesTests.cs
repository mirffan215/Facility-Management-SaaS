using Fms.Visitors.Domain;

namespace Fms.UnitTests;

public sealed class VisitorRulesTests
{
    private static readonly DateTimeOffset At = new(2027, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("José  García", "jose garcia")]
    [InlineData("  JOSE   GARCIA ", "jose garcia")]
    [InlineData("Jose-Garcia", "jose garcia")]
    [InlineData("O'Brien", "o brien")]
    [InlineData("Müller", "muller")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Latin_names_match_regardless_of_case_accents_spacing_and_punctuation(string name, string expected) => Assert.Equal(expected, VisitorRules.Normalize(name));

    [Fact]
    public void Arabic_spellings_of_the_same_name_match()
    {
        // alef with hamza / plain alef, ya / alef maqsura, ta marbuta / ha, vowel marks and tatweel.
        Assert.Equal(VisitorRules.Normalize("أحمد"), VisitorRules.Normalize("احمد"));
        Assert.Equal(VisitorRules.Normalize("إبراهيم"), VisitorRules.Normalize("ابراهيم"));
        Assert.Equal(VisitorRules.Normalize("مصطفى"), VisitorRules.Normalize("مصطفي"));
        Assert.Equal(VisitorRules.Normalize("فاطمة"), VisitorRules.Normalize("فاطمه"));
        Assert.Equal(VisitorRules.Normalize("مُحَمَّد"), VisitorRules.Normalize("محمد"));
        Assert.Equal(VisitorRules.Normalize("محـــمد"), VisitorRules.Normalize("محمد"));
        Assert.Equal(VisitorRules.Normalize("محمد  علي"), VisitorRules.Normalize("محمد علي"));
    }

    [Fact]
    public void Different_people_do_not_match()
    {
        Assert.NotEqual(VisitorRules.Normalize("Jose Garcia"), VisitorRules.Normalize("Jose Garza"));
        Assert.NotEqual(VisitorRules.Normalize("محمد علي"), VisitorRules.Normalize("محمد عمر"));
        Assert.NotEqual(VisitorRules.Normalize("Ann Lee"), VisitorRules.Normalize("Lee Ann")); // word order matters
    }

    [Theory]
    [InlineData(-13, false)] // more than half a day early
    [InlineData(-12, true)]
    [InlineData(0, true)]
    [InlineData(23, true)] // inside the default 24 hour stay
    [InlineData(25, false)] // over
    public void Check_in_is_open_from_half_a_day_before_until_the_visit_is_over(int hoursFromExpected, bool open) =>
        Assert.Equal(open, VisitorRules.CanCheckIn(VisitStatus.Expected, At, null, At.AddHours(hoursFromExpected)));

    [Fact]
    public void An_explicit_end_replaces_the_default_stay()
    {
        Assert.True(VisitorRules.CanCheckIn(VisitStatus.Expected, At, At.AddHours(2), At.AddHours(2)));
        Assert.False(VisitorRules.CanCheckIn(VisitStatus.Expected, At, At.AddHours(2), At.AddHours(3)));
    }

    [Theory]
    [InlineData(VisitStatus.CheckedIn)]
    [InlineData(VisitStatus.CheckedOut)]
    [InlineData(VisitStatus.Cancelled)]
    [InlineData(VisitStatus.NoShow)]
    public void Only_an_expected_visit_can_be_checked_in(VisitStatus status) => Assert.False(VisitorRules.CanCheckIn(status, At, null, At));

    [Fact]
    public void An_expected_visit_whose_time_is_over_is_a_no_show_and_nothing_else_is()
    {
        Assert.True(VisitorRules.IsNoShow(VisitStatus.Expected, At, null, At.AddHours(25)));
        Assert.False(VisitorRules.IsNoShow(VisitStatus.Expected, At, null, At.AddHours(23)));
        Assert.False(VisitorRules.IsNoShow(VisitStatus.CheckedIn, At, null, At.AddDays(5)));
        Assert.False(VisitorRules.IsNoShow(VisitStatus.Cancelled, At, null, At.AddDays(5)));
        Assert.True(VisitorRules.IsNoShow(VisitStatus.Expected, At, At.AddHours(2), At.AddHours(3)));
    }

    [Fact]
    public void Details_are_due_for_removal_after_the_retention_period()
    {
        Assert.False(VisitorRules.IsDueForRemoval(At, 30, At.AddDays(29)));
        Assert.True(VisitorRules.IsDueForRemoval(At, 30, At.AddDays(31)));
    }

    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUv", "AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://fms.example.com/visit/AbCdEfGhIjKlMnOpQrStUv", "AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://fms.example.com/visit/AbCdEfGhIjKlMnOpQrStUv/", "AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("  /visit/AbCdEfGhIjKlMnOpQrStUv?x=1#y ", "AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("short", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("https://fms.example.com/visit/has spaces and symbols!!!!!!", null)]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvAbCdEfGhIjKlMnOpQrStUvAbCdEfGhIjKlMnOpQrStUvAbCdEf", null)]
    public void The_code_is_found_in_what_a_scanner_typed(string? scanned, string? expected) => Assert.Equal(expected, VisitorRules.CodeFrom(scanned));
}
