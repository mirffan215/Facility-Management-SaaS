using System.Net;
using System.Text;
using Fms.Ai.Domain;
using Fms.Ai.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fms.UnitTests;

public sealed class AiRulesTests
{
    [Theory]
    [InlineData("Call me on +966 50 123 4567 please", "[number]")]
    [InlineData("mail ahmed.ali@example.com now", "[email]")]
    [InlineData("see https://intranet.example.com/x?id=1 for photo", "[link]")]
    [InlineData("see www.example.com/photo", "[link]")]
    [InlineData("my id is 1234567890", "[number]")]
    public void Personal_details_are_replaced(string text, string marker)
    {
        var clean = AiRules.Clean(text);
        Assert.Contains(marker, clean);
        Assert.DoesNotContain("ahmed", clean);
        Assert.DoesNotContain("1234567", clean);
        Assert.DoesNotContain("example.com", clean);
        Assert.DoesNotContain("4567", clean);
    }

    [Fact]
    public void Ordinary_numbers_and_work_order_numbers_are_kept()
    {
        Assert.Equal("Pump 3 on floor 12 leaks, see WO-000123", AiRules.Clean("Pump 3 on floor 12 leaks, see WO-000123"));
        Assert.Equal("Room 204, 15 litres", AiRules.Clean("Room 204, 15 litres"));
    }

    [Fact]
    public void Text_is_squeezed_cleaned_of_control_characters_and_cut()
    {
        Assert.Equal("a b c", AiRules.Clean("a \u0000  b\u0007\n\n c"));
        Assert.Equal("", AiRules.Clean("   "));
        Assert.Equal("", AiRules.Clean(null));
        var cut = AiRules.Clean(new string('x', 5000), 100);
        Assert.Equal(101, cut.Length);
        Assert.EndsWith("…", cut);
    }

    private static readonly string[] Codes = ["PLUMB", "ELEC", "HVAC"];

    [Fact]
    public void A_good_triage_reply_is_read()
    {
        var a = AiRules.ParseTriage("{\"categoryCode\":\"elec\",\"priority\":\"high\",\"confidence\":0.8,\"reason\":\"Sparks from a socket.\"}", Codes)!;
        Assert.Equal("ELEC", a.CategoryCode);
        Assert.Equal("High", a.Priority);
        Assert.Equal(0.8, a.Confidence);
        Assert.Equal("Sparks from a socket.", a.Reason);
    }

    [Fact]
    public void JSON_inside_a_sentence_or_a_code_fence_is_found()
    {
        var a = AiRules.ParseTriage("Here you go:\n```json\n{\"categoryCode\":\"HVAC\",\"priority\":\"Low\",\"confidence\":1,\"reason\":\"Warm room\"}\n```", Codes);
        Assert.Equal("HVAC", a!.CategoryCode);
    }

    [Theory]
    [InlineData("{\"categoryCode\":\"MADEUP\",\"priority\":\"Critical\",\"confidence\":1,\"reason\":\"x\"}")] // neither value is one we have
    [InlineData("I cannot help with that")]
    [InlineData("{not json}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    [InlineData(null)]
    public void A_reply_with_nothing_usable_gives_nothing(string? reply) => Assert.Null(AiRules.ParseTriage(reply, Codes));

    [Fact]
    public void An_invented_value_is_dropped_but_the_valid_part_is_kept()
    {
        var a = AiRules.ParseTriage("{\"categoryCode\":\"ROOF\",\"priority\":\"Urgent\",\"confidence\":5,\"reason\":\"Water\"}", Codes)!;
        Assert.Null(a.CategoryCode);
        Assert.Equal("Urgent", a.Priority);
        Assert.Equal(1, a.Confidence); // clamped
    }

    [Fact]
    public void A_reason_is_cleaned_like_any_other_text()
    {
        var a = AiRules.ParseTriage("{\"categoryCode\":\"ELEC\",\"priority\":\"Low\",\"reason\":\"Ring 0501234567 or mail x@y.com\"}", Codes)!;
        Assert.DoesNotContain("0501234567", a.Reason);
        Assert.DoesNotContain("x@y.com", a.Reason);
    }

    [Fact]
    public void A_summary_is_plain_text()
    {
        var s = AiRules.PlainSummary("**Leak** in <script>alert(1)</script> `room` # 4.\nFixed.");
        Assert.DoesNotContain("<", s);
        Assert.DoesNotContain("*", s);
        Assert.DoesNotContain("`", s);
        Assert.Contains("Leak", s);
        Assert.True(AiRules.PlainSummary(new string('a', 9000)).Length <= AiRules.MaxSummaryChars + 1);
    }

    // ---- the Claude provider, against a stand-in server

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return respond(request, Body);
        }
    }

    private static ClaudeProvider Provider(Handler h, Action<AiOptions>? edit = null)
    {
        var o = new AiOptions { Provider = "Claude", ApiKey = "sk-test-secret", Endpoint = "https://api.example.test", Model = "claude-test" };
        edit?.Invoke(o);
        return new ClaudeProvider(Options.Create(o), NullLogger<ClaudeProvider>.Instance, h);
    }

    [Fact]
    public async Task The_provider_sends_the_key_in_a_header_and_reads_text_and_token_counts()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"},{\"type\":\"text\",\"text\":\" there\"}],\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}", Encoding.UTF8, "application/json") });
        var r = await Provider(h).CompleteAsync(new AiRequest("be brief", "hi", 100));
        Assert.True(r.Ok);
        Assert.Equal("Hello there", r.Text);
        Assert.Equal((12, 3), (r.InputTokens, r.OutputTokens));
        Assert.Equal("https://api.example.test/v1/messages", h.Request!.RequestUri!.ToString());
        Assert.Equal("sk-test-secret", h.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.Request.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain("sk-test-secret", h.Body);
        Assert.Contains("\"model\":\"claude-test\"", h.Body);
        Assert.Contains("be brief", h.Body);
    }

    [Fact]
    public async Task The_provider_caps_the_output_size_to_the_operators_setting()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content\":[],\"usage\":{}}") });
        await Provider(h, o => o.MaxOutputTokens = 50).CompleteAsync(new AiRequest("s", "u", 5000));
        Assert.Contains("\"max_tokens\":50", h.Body);
    }

    [Fact]
    public async Task A_failing_service_gives_a_short_error_without_its_body_or_the_key()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("secret internal trace sk-test-secret") });
        var r = await Provider(h).CompleteAsync(new AiRequest("s", "u", 10));
        Assert.False(r.Ok);
        Assert.Null(r.Text);
        Assert.DoesNotContain("secret", r.Error);
        Assert.Contains("500", r.Error);
    }

    [Fact]
    public async Task A_network_failure_is_an_error_result_not_an_exception()
    {
        var h = new Handler((_, _) => throw new HttpRequestException("boom"));
        var r = await Provider(h).CompleteAsync(new AiRequest("s", "u", 10));
        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData(null, "https://api.example.test", false)]
    [InlineData("", "https://api.example.test", false)]
    [InlineData("key", "http://api.example.test", false)] // the key is never sent over plain http to another host
    [InlineData("key", "not a url", false)]
    [InlineData("key", "https://api.example.test", true)]
    [InlineData("key", "http://localhost:9999", true)]
    public void The_provider_is_configured_only_with_a_key_and_a_safe_address(string? key, string endpoint, bool expected) =>
        Assert.Equal(expected, Provider(new Handler((_, _) => new HttpResponseMessage()), o => { o.ApiKey = key; o.Endpoint = endpoint; }).IsConfigured);

    [Fact]
    public async Task Without_a_provider_nothing_is_available()
    {
        var none = new NoAiProvider();
        Assert.False(none.IsConfigured);
        Assert.False((await none.CompleteAsync(new AiRequest("s", "u", 1))).Ok);
    }
}

public sealed class RepeatMathTests
{
    [Fact]
    public void The_average_gap_is_the_span_over_the_number_of_gaps()
    {
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(15.0, Fms.Work.Services.RepeatMath.AverageDaysBetween(first, first.AddDays(30), 3));
        Assert.Equal(10.0, Fms.Work.Services.RepeatMath.AverageDaysBetween(first, first.AddDays(10), 2));
        Assert.Null(Fms.Work.Services.RepeatMath.AverageDaysBetween(first, first, 1));
    }
}

public sealed class SimilarMathTests
{
    [Fact]
    public void Meaningful_words_are_kept_and_filler_and_short_words_dropped()
    {
        Assert.Equal(new[] { "chiller", "leaking", "roof" }.Order(), Fms.Work.Services.SimilarMath.Words("The Chiller is leaking, roof! 12").Order());
        Assert.Empty(Fms.Work.Services.SimilarMath.Words("and the of it"));
        Assert.Empty(Fms.Work.Services.SimilarMath.Words(null));
    }

    [Theory]
    [InlineData("Water leaking from ceiling", "Ceiling leaking again in corridor", 2)]
    [InlineData("Door closer loose", "Broken window", 0)]
    [InlineData("تسرب مياه من السقف", "السقف يتسرب منه الماء", 1)]
    [InlineData("AC not cooling", "ac NOT cooling in room 4", 1)] // "ac" is too short and "not" is filler
    public void Shared_words_are_counted_ignoring_case(string a, string b, int expected) =>
        Assert.Equal(expected, Fms.Work.Services.SimilarMath.SharedWords(a, b));
}

public sealed class AssignmentRankerTests
{
    private static Fms.Work.Domain.CandidateStats C(string name, int asset = 0, int category = 0, int place = 0, int open = 0) => new(Guid.NewGuid(), name, asset, category, place, open);

    [Fact]
    public void The_same_asset_counts_most_then_the_kind_of_work_then_the_place_and_load_counts_against()
    {
        Assert.Equal(5 * 2 + 3 * 1 + 1 * 4 - 2 * 3, Fms.Work.Domain.AssignmentRanker.Score(C("A", asset: 2, category: 1, place: 4, open: 3)));
        var ranked = Fms.Work.Domain.AssignmentRanker.Rank([C("Placeman", place: 2), C("Assetman", asset: 1), C("Categoryman", category: 1)]);
        Assert.Equal(["Assetman", "Categoryman", "Placeman"], ranked.Select(r => r.Stats.Name).ToArray());
    }

    [Fact]
    public void A_free_person_without_history_beats_a_busy_one_without_history_and_a_very_busy_expert_can_lose()
    {
        var ranked = Fms.Work.Domain.AssignmentRanker.Rank([C("Busy", open: 4), C("Free"), C("BusyExpert", asset: 1, open: 5)]);
        Assert.Equal(["Free", "BusyExpert", "Busy"], ranked.Select(r => r.Stats.Name).ToArray()); // 0, 5-10, -8
    }

    [Fact]
    public void Ties_go_to_the_lighter_load_then_the_name_and_the_list_is_cut()
    {
        var ranked = Fms.Work.Domain.AssignmentRanker.Rank([C("Zed"), C("Amy"), C("Bob", category: 1, open: 1), C("Cy", category: 1)], max: 3);
        Assert.Equal(3, ranked.Count);
        Assert.Equal(["Cy", "Bob", "Amy"], ranked.Select(r => r.Stats.Name).ToArray()); // 3, 1, 0 (Amy before Zed)
    }

    [Fact]
    public void Vendors_with_a_target_come_first_quickest_first_then_by_past_jobs_then_name()
    {
        var v = (string n, int? mins, int past) => new Fms.Work.Domain.VendorStats(Guid.NewGuid(), n, "C-" + n, mins, past);
        var ranked = Fms.Work.Domain.AssignmentRanker.RankVendors([v("NoTargetBusy", null, 9), v("Slow", 480, 0), v("Fast", 120, 0), v("NoTargetNone", null, 0), v("FastExperienced", 120, 3)]);
        Assert.Equal(["FastExperienced", "Fast", "Slow"], ranked.Select(r => r.Name).ToArray());
        Assert.Equal(["FastExperienced", "Fast", "Slow", "NoTargetBusy", "NoTargetNone"], Fms.Work.Domain.AssignmentRanker.RankVendors([v("NoTargetBusy", null, 9), v("Slow", 480, 0), v("Fast", 120, 0), v("NoTargetNone", null, 0), v("FastExperienced", 120, 3)], 10).Select(r => r.Name).ToArray());
    }
}
