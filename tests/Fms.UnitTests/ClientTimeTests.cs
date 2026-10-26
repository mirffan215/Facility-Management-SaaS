using Fms.Work.Domain;

namespace Fms.UnitTests;

public sealed class ClientTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_claim_means_now() => Assert.Equal(Now, ClientTime.Resolve(null, Now.AddHours(-1), Now));

    [Fact]
    public void A_claim_between_the_last_change_and_now_is_trusted()
    {
        var claimed = Now.AddMinutes(-20);
        Assert.Equal(claimed, ClientTime.Resolve(claimed, Now.AddHours(-2), Now));
        Assert.Equal(Now, ClientTime.Resolve(Now, Now.AddHours(-2), Now));
    }

    [Fact]
    public void A_claim_in_the_future_is_lowered_to_now() => Assert.Equal(Now, ClientTime.Resolve(Now.AddDays(3), Now.AddHours(-2), Now));

    [Fact]
    public void A_claim_before_the_last_change_is_raised_to_it()
    {
        var lastChange = Now.AddHours(-2);
        Assert.Equal(lastChange, ClientTime.Resolve(Now.AddHours(-5), lastChange, Now));
    }

    [Fact]
    public void A_claim_older_than_a_week_is_limited_to_a_week()
    {
        var lastChange = Now.AddDays(-30);
        Assert.Equal(Now - ClientTime.MaxBackdate, ClientTime.Resolve(Now.AddDays(-20), lastChange, Now));
    }

    [Fact]
    public void A_last_change_that_is_somehow_ahead_of_the_server_clock_falls_back_to_now() =>
        Assert.Equal(Now, ClientTime.Resolve(Now.AddMinutes(-1), Now.AddMinutes(5), Now));
}
