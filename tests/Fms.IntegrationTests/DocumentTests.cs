using System.IO.Compression;
using Fms.BuildingBlocks.Storage;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

internal static class TestFiles
{
    public static byte[] Pdf(string marker = "x") => Encoding.ASCII.GetBytes($"%PDF-1.7\n% {marker}\n%%EOF");
    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static byte[] Exe() => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0, 0, 0, 0xFF, 0xFF];

    public static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("<x/>");
            }
        }

        return ms.ToArray();
    }
}

[Collection(SqlCollection.Name)]
public sealed class DocumentTests(SqlServerFixture sql)
{
    private sealed class Scanner(bool clean) : IFileScanner
    {
        public Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default) => Task.FromResult(new ScanOutcome(clean, clean ? null : "Test.Threat"));
    }

    private async Task<AssetDto> NewAssetAsync(Guid tenant)
    {
        await using var f = FacilitiesScope.For(sql, tenant);
        var site = await f.NewAsync(LocationType.Site, null);
        var cls = await f.NewClassAsync();
        return await f.NewAssetAsync(cls.Id, site.Id, active: true);
    }

    private static async Task<byte[]> ReadAsync(Stream s)
    {
        await using var stream = s;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task A_file_round_trips_with_a_content_hash_and_a_tenant_scoped_storage_key()
    {
        var asset = await NewAssetAsync(TenantA);
        var bytes = TestFiles.Pdf(Guid.NewGuid().ToString());
        await using var f = FacilitiesScope.For(sql, TenantA);

        var uploaded = await f.Documents.UploadAsync(asset.Id, DocumentKind.Manual, " Service manual ", "Chiller manual.pdf", new MemoryStream(bytes));

        Assert.True(uploaded.IsOk, uploaded.Message);
        var doc = uploaded.Value!;
        Assert.Equal("application/pdf", doc.ContentType);
        Assert.Equal(bytes.Length, doc.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), doc.Sha256);
        Assert.Equal("Service manual", doc.Description);
        Assert.Equal(f.UserId, doc.UploadedBy);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var downloaded = (await g.Documents.DownloadAsync(doc.Id)).Value!;
        Assert.Equal(bytes, await ReadAsync(downloaded.Content));

        await using var check = sql.Facilities(TenantA);
        var stored = await check.AssetDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        Assert.StartsWith(TenantA.ToString("N") + "/asset-documents/", stored.StorageKey);
        Assert.DoesNotContain("manual", stored.StorageKey, StringComparison.OrdinalIgnoreCase); // no user input in the path
    }

    [Fact]
    public async Task The_declared_name_and_type_are_ignored_and_the_content_decides()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);

        var disguised = (await f.Documents.UploadAsync(asset.Id, DocumentKind.Photo, null, "..\\..\\evil.html", new MemoryStream(TestFiles.Png()))).Value!;
        Assert.Equal("image/png", disguised.ContentType);
        Assert.Equal("evil.html.png", disguised.FileName);

        var arabic = (await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "تقرير.docx", new MemoryStream(TestFiles.Docx()))).Value!;
        Assert.Equal("تقرير.docx", arabic.FileName);
        Assert.Contains("wordprocessingml", arabic.ContentType);
    }

    [Fact]
    public async Task Unsafe_empty_oversized_and_infected_files_are_refused_and_nothing_is_stored()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA, options: new FacilitiesOptions { MaxDocumentBytes = 2048 });

        var exe = await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "setup.pdf", new MemoryStream(TestFiles.Exe()));
        Assert.Equal(ResultStatus.UnsupportedType, exe.Status);
        Assert.Equal(ResultStatus.Invalid, (await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "empty.pdf", new MemoryStream([]))).Status);
        var big = Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(new byte[4096]).ToArray();
        Assert.Equal(ResultStatus.TooLarge, (await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "big.pdf", new MemoryStream(big))).Status);
        Assert.Equal(ResultStatus.Invalid, (await f.Documents.UploadAsync(asset.Id, (DocumentKind)99, null, "a.pdf", new MemoryStream(TestFiles.Pdf()))).Status);
        Assert.Equal(ResultStatus.Invalid, (await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, new string('d', 501), "a.pdf", new MemoryStream(TestFiles.Pdf()))).Status);

        await using var infected = FacilitiesScope.For(sql, TenantA, scanner: new Scanner(clean: false));
        Assert.Equal(ResultStatus.Invalid, (await infected.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "a.pdf", new MemoryStream(TestFiles.Pdf()))).Status);

        await using var check = FacilitiesScope.For(sql, TenantA);
        Assert.Empty((await check.Documents.ListAsync(asset.Id)).Value!);
    }

    [Fact]
    public async Task The_size_limit_counts_real_bytes_even_when_the_stream_hides_its_length()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA, options: new FacilitiesOptions { MaxDocumentBytes = 1024 });
        var big = Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(new byte[5000]).ToArray();

        var result = await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "x.pdf", new NonSeekable(big));

        Assert.Equal(ResultStatus.TooLarge, result.Status);
    }

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_asset_has_a_file_limit_and_a_retired_asset_takes_no_more_files()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA, options: new FacilitiesOptions { MaxDocumentsPerAsset = 2 });
        for (var i = 0; i < 2; i++) Assert.True((await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "a.pdf", new MemoryStream(TestFiles.Pdf(i.ToString())))).IsOk);
        Assert.Equal(ResultStatus.Conflict, (await f.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "a.pdf", new MemoryStream(TestFiles.Pdf("3")))).Status);

        var other = await NewAssetAsync(TenantA);
        await using (var g = FacilitiesScope.For(sql, TenantA))
            Assert.True((await g.Assets.ChangeStatusAsync(other.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "x", other.RowVersion))).IsOk);
        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Documents.UploadAsync(other.Id, DocumentKind.Other, null, "a.pdf", new MemoryStream(TestFiles.Pdf()))).Status);
        Assert.Equal(ResultStatus.NotFound, (await h.Documents.UploadAsync(Guid.NewGuid(), DocumentKind.Other, null, "a.pdf", new MemoryStream(TestFiles.Pdf()))).Status);
    }

    [Fact]
    public async Task Deleting_hides_the_file_but_keeps_the_evidence_and_requires_a_reason()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var doc = (await f.Documents.UploadAsync(asset.Id, DocumentKind.Certificate, null, "cert.pdf", new MemoryStream(TestFiles.Pdf("cert")))).Value!;

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Invalid, (await g.Documents.DeleteAsync(doc.Id, new DeleteDocumentRequest(" "))).Status);
        Assert.True((await g.Documents.DeleteAsync(doc.Id, new DeleteDocumentRequest("expired certificate"))).IsOk);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Empty((await h.Documents.ListAsync(asset.Id)).Value!);
        Assert.Equal(ResultStatus.NotFound, (await h.Documents.DownloadAsync(doc.Id)).Status);
        Assert.Equal(ResultStatus.NotFound, (await h.Documents.DeleteAsync(doc.Id, new DeleteDocumentRequest("again"))).Status);

        await using var check = sql.Facilities(TenantA);
        var row = await check.AssetDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal("expired certificate", row.DeleteReason);
        Assert.NotNull(await sql.Blobs.GetAsync(row.StorageKey)); // the stored bytes are kept
    }

    [Fact]
    public async Task Upload_download_and_delete_are_audited()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var doc = (await f.Documents.UploadAsync(asset.Id, DocumentKind.Manual, null, "m.pdf", new MemoryStream(TestFiles.Pdf("audit")))).Value!;
        await using (var g = FacilitiesScope.For(sql, TenantA)) await (await g.Documents.DownloadAsync(doc.Id)).Value!.Content.DisposeAsync();
        await using (var h = FacilitiesScope.For(sql, TenantA)) await h.Documents.DeleteAsync(doc.Id, new DeleteDocumentRequest("obsolete"));

        await using var check = sql.App(TenantA);
        var actions = await check.AuditEvents.Where(e => e.EntityId == doc.Id.ToString()).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync();
        Assert.Equal(["document.uploaded", "document.downloaded", "document.deleted"], actions);
    }

    [Fact]
    public async Task Another_tenant_can_neither_list_download_nor_delete_a_file_and_the_database_agrees()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var a = FacilitiesScope.For(sql, TenantA);
        var doc = (await a.Documents.UploadAsync(asset.Id, DocumentKind.Other, null, "secret.pdf", new MemoryStream(TestFiles.Pdf("secret")))).Value!;

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.Equal(ResultStatus.NotFound, (await b.Documents.ListAsync(asset.Id)).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Documents.DownloadAsync(doc.Id)).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Documents.DeleteAsync(doc.Id, new DeleteDocumentRequest("x"))).Status);
        Assert.Equal(0, await b.Db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM facilities.AssetDocuments WHERE Id = {doc.Id}").SingleAsync());

        // Even holding the exact storage key, the tenant-scoped store refuses it.
        await using var check = sql.Facilities(TenantA);
        var key = (await check.AssetDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).StorageKey;
        var storeB = new Fms.BuildingBlocks.Storage.TenantScopedFileStore(sql.Blobs, TenantContextFor(TenantB));
        await Assert.ThrowsAsync<Fms.BuildingBlocks.Tenancy.TenantViolationException>(() => storeB.GetAsync(key));
    }

    private static Fms.BuildingBlocks.Tenancy.TenantContext TenantContextFor(Guid tenant)
    {
        var t = new Fms.BuildingBlocks.Tenancy.TenantContext();
        t.SetTenant(tenant);
        return t;
    }
}

[Collection(SqlCollection.Name)]
public sealed class DocumentApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?>
    {
        ["Storage:Provider"] = "InMemory",
        ["Facilities:MaxDocumentBytes"] = "200000",
    });

    public void Dispose() => _api.Dispose();

    private async Task<(HttpClient Manager, AssetDto Asset)> SetupAsync(Guid tenant)
    {
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "S" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "C" }));
        return (manager, await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "Pump", assetClassId = cls.Id, locationId = site.Id, startActive = true })));
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName = "manual.pdf", string? kind = "Manual", string? declaredType = "application/pdf", string fieldName = "file")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        if (declaredType is not null) file.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
        form.Add(file, fieldName, fileName);
        if (kind is not null) form.Add(new StringContent(kind), "kind");
        form.Add(new StringContent("from test"), "description");
        return form;
    }

    [Fact]
    public async Task Upload_then_download_returns_the_same_bytes_as_a_safe_attachment()
    {
        var (manager, asset) = await SetupAsync(TenantA);
        var bytes = TestFiles.Pdf(Guid.NewGuid().ToString());

        var created = await manager.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(bytes, "دليل التشغيل.pdf"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = await ReadAsync<DocumentDto>(created);
        Assert.Equal("دليل التشغيل.pdf", doc.FileName);
        Assert.Equal(DocumentKind.Manual, doc.Kind);

        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var download = await technician.GetAsync($"/api/v1/documents/{doc.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("دليل التشغيل.pdf", download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", download.Headers.CacheControl!.ToString());

        var listed = await technician.GetFromJsonAsync<DocumentDto[]>($"/api/v1/assets/{asset.Id}/documents", Json);
        Assert.Single(listed!);
    }

    [Fact]
    public async Task Only_document_managers_can_upload_or_delete_while_readers_can_download()
    {
        var (manager, asset) = await SetupAsync(TenantA);
        var doc = await ReadAsync<DocumentDto>(await manager.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(TestFiles.Pdf("p"))));
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);

        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(TestFiles.Pdf("q")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/delete", new { reason = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync($"/api/v1/documents/{doc.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync($"/api/v1/assets/{asset.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(manager, $"/api/v1/documents/{doc.Id}/delete", new { reason = "obsolete" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await technician.GetAsync($"/api/v1/documents/{doc.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Dangerous_missing_and_oversized_uploads_get_precise_errors()
    {
        var (manager, asset) = await SetupAsync(TenantA);
        var url = $"/api/v1/assets/{asset.Id}/documents";

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await manager.PostAsync(url, Form(TestFiles.Exe(), "setup.pdf", declaredType: "application/pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await manager.PostAsync(url, Form(Encoding.UTF8.GetBytes("MZ"), "a.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync(url, Form(TestFiles.Pdf(), fieldName: "upload"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync(url, Form(TestFiles.Pdf(), kind: "Nonsense"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync(url, Form([]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PostAsync(manager, url, new { file = "x" })).StatusCode);

        var big = Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(new byte[300_000]).ToArray();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await manager.PostAsync(url, Form(big))).StatusCode);

        var two = Form(TestFiles.Pdf("1"));
        two.Add(new ByteArrayContent(TestFiles.Pdf("2")), "file", "second.pdf");
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync(url, two)).StatusCode);
        Assert.Empty((await manager.GetFromJsonAsync<DocumentDto[]>(url, Json))!);
    }

    [Fact]
    public async Task Another_tenant_cannot_reach_a_file_through_the_api()
    {
        var (managerA, asset) = await SetupAsync(TenantA);
        var doc = await ReadAsync<DocumentDto>(await managerA.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(TestFiles.Pdf("private"))));
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/documents/{doc.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/assets/{asset.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(TestFiles.Pdf("plant")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(managerB, $"/api/v1/documents/{doc.Id}/delete", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401_for_documents()
    {
        var (_, asset) = await SetupAsync(TenantA);
        var anonymous = _api.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/assets/{asset.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/assets/{asset.Id}/documents", Form(TestFiles.Pdf()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/documents/{Guid.NewGuid()}/download")).StatusCode);
    }
}
