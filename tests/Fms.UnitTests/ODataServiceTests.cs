using System.Text;
using System.Xml.Linq;
using Fms.BuildingBlocks.Reporting;
using Fms.Reporting.Services;

namespace Fms.UnitTests;

public sealed class ODataServiceTests
{
    [Fact]
    public void A_date_filter_on_the_time_column_becomes_a_range_with_an_exclusive_upper_end()
    {
        var (from, to, error) = ODataService.ParseFilter("RequestedAt ge 2027-03-01T00:00:00Z and RequestedAt lt 2027-04-01T00:00:00Z", "RequestedAt");
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero), to);
    }

    [Fact]
    public void Greater_than_and_less_or_equal_are_adjusted_by_one_tick_so_the_range_stays_exact()
    {
        var (from, to, _) = ODataService.ParseFilter("At gt 2027-03-01T00:00:00Z and At le 2027-03-31T00:00:00Z", "At");
        Assert.Equal(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1), from);
        Assert.Equal(new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero).AddTicks(1), to);
    }

    [Fact]
    public void Several_bounds_on_one_side_keep_the_tightest()
    {
        var (from, to, _) = ODataService.ParseFilter("At ge 2027-01-01T00:00:00Z and At ge 2027-02-01T00:00:00Z and At lt 2027-06-01T00:00:00Z and At lt 2027-05-01T00:00:00Z", "At");
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2027, 5, 1, 0, 0, 0, TimeSpan.Zero), to);
    }

    [Theory]
    [InlineData("Status eq 'Open'")] // another column
    [InlineData("At eq 2027-03-01T00:00:00Z")] // an unsupported operator
    [InlineData("At ge yesterday")] // not a date
    [InlineData("At ge 2027-03-01T00:00:00Z or At lt 2027-01-01T00:00:00Z")] // "or" is not supported
    [InlineData("contains(Title,'x')")]
    public void Anything_else_is_refused_not_ignored(string filter)
    {
        var (_, _, error) = ODataService.ParseFilter(filter, "At");
        Assert.NotNull(error);
    }

    [Fact]
    public void A_dataset_without_a_time_column_cannot_be_filtered() => Assert.NotNull(ODataService.ParseFilter("At ge 2027-03-01T00:00:00Z", null).Error);

    [Theory]
    [InlineData("Basic dXNlcjpmbXNmX3NlY3JldA==", "fmsf_secret")] // user:fmsf_secret
    [InlineData("basic OmZtc2Zfc2VjcmV0", "fmsf_secret")] // :fmsf_secret, no user name
    [InlineData("Basic Zm1zZl9zZWNyZXQ=", "fmsf_secret")] // fmsf_secret with no colon
    [InlineData("Bearer abc", null)]
    [InlineData("Basic !!!not-base64!!!", null)]
    [InlineData(null, null)]
    public void The_secret_is_the_basic_password_and_the_user_name_is_ignored(string? header, string? expected) => Assert.Equal(expected, FeedService.SecretFrom(header, null));

    [Fact]
    public void An_api_key_header_wins_over_basic() => Assert.Equal("fmsf_k", FeedService.SecretFrom("Basic dXNlcjpvdGhlcg==", " fmsf_k "));

    [Fact]
    public void The_metadata_document_is_well_formed_and_lists_every_set_and_column_with_its_type()
    {
        var sets = new IDatasetProvider[] { new Fake("WorkOrders", [new("Id", "Edm.Guid"), new("Number", "Edm.Int32")]), new Fake("Visits", [new("Id", "Edm.Guid"), new("Kind", "Edm.String")]) };
        var doc = XDocument.Parse(ODataService.Metadata(sets));
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";
        Assert.Equal(["WorkOrders", "Visits"], doc.Descendants(edm + "EntitySet").Select(e => (string)e.Attribute("Name")!).ToArray());
        var type = doc.Descendants(edm + "EntityType").First(e => (string)e.Attribute("Name")! == "WorkOrdersRow");
        Assert.Equal("Id", (string)type.Element(edm + "Key")!.Element(edm + "PropertyRef")!.Attribute("Name")!);
        Assert.Equal(["Edm.Guid", "Edm.Int32"], type.Elements(edm + "Property").Select(p => (string)p.Attribute("Type")!).ToArray());
        Assert.Equal("false", (string)type.Elements(edm + "Property").First().Attribute("Nullable")!);
    }

    [Fact]
    public async Task Paging_options_and_selection_are_validated_before_anything_is_read()
    {
        var set = new Fake("T", [new("Id", "Edm.Guid"), new("A", "Edm.String")]);
        foreach (var bad in new[] { "$top=0", "$top=1001", "$top=abc", "$skip=-1", "$select=Nope", "$filter=A eq 1" })
        {
            var kv = bad.Split('=', 2);
            var page = await ODataService.ReadAsync(set, "http://x/odata", new Dictionary<string, string> { [kv[0]] = kv[1] }, CancellationToken.None);
            Assert.False(page.Ok, bad);
            Assert.Equal(400, page.Status);
            Assert.Contains("\"error\"", page.Json);
        }
    }

    private sealed class Fake(string name, IReadOnlyList<DatasetColumn> columns) : IDatasetProvider
    {
        public string Name => name;
        public string Permission => "x";
        public IReadOnlyList<DatasetColumn> Columns => columns;
        public string? TimeColumn => null;
        public Task<DatasetPage> ReadAsync(DatasetQuery query, bool withTotal, CancellationToken ct = default) => Task.FromResult(new DatasetPage([], null));
    }
}
