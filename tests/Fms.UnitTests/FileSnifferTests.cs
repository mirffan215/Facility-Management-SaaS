using System.IO.Compression;
using Fms.BuildingBlocks.Storage;
using System.Text;
using Fms.Facilities.Domain;

namespace Fms.UnitTests;

public static class SampleFiles
{
    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0];
    public static byte[] WebP() => [.. "RIFF"u8, 0x20, 0, 0, 0, .. "WEBPVP8 "u8, 0, 0, 0, 0];
    public static byte[] Text(string text = "Annual service notes\nNext visit: March") => Encoding.UTF8.GetBytes(text);

    public static byte[] Zip(params string[] entryNames)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entryNames)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("<x/>");
            }
        }

        return ms.ToArray();
    }

    public static byte[] Docx() => Zip("[Content_Types].xml", "word/document.xml");
    public static byte[] Xlsx() => Zip("[Content_Types].xml", "xl/workbook.xml");
}

public sealed class FileSnifferTests
{
    [Fact]
    public void Allowed_formats_are_recognised_by_content()
    {
        Assert.Equal(("application/pdf", ".pdf"), Pair(SampleFiles.Pdf()));
        Assert.Equal(("image/png", ".png"), Pair(SampleFiles.Png()));
        Assert.Equal(("image/jpeg", ".jpg"), Pair(SampleFiles.Jpeg()));
        Assert.Equal(("image/webp", ".webp"), Pair(SampleFiles.WebP()));
        Assert.Equal(("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"), Pair(SampleFiles.Docx()));
        Assert.Equal(("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"), Pair(SampleFiles.Xlsx()));
        Assert.Equal(("text/plain", ".txt"), Pair(SampleFiles.Text()));
        Assert.Equal(("text/plain", ".txt"), Pair(SampleFiles.Text("تقرير الصيانة السنوي\r\nالزيارة القادمة: مارس")));
    }

    private static (string, string) Pair(byte[] bytes)
    {
        var d = FileSniffer.Detect(bytes);
        Assert.NotNull(d);
        return (d.ContentType, d.Extension);
    }

    [Fact]
    public void Executables_scripts_and_markup_are_refused()
    {
        Assert.Null(FileSniffer.Detect([0x4D, 0x5A, 0x90, 0x00, 0x03, 0, 0, 0]));                       // Windows executable
        Assert.Null(FileSniffer.Detect([0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0]));                           // ELF
        Assert.Null(FileSniffer.Detect(Encoding.UTF8.GetBytes("<!DOCTYPE html><script>alert(1)</script>").Prepend((byte)0).ToArray())); // NUL-prefixed
        Assert.Null(FileSniffer.Detect([0x1F, 0x8B, 8, 0, 0, 0, 0, 0]));                                 // gzip
        Assert.Null(FileSniffer.Detect(Encoding.ASCII.GetBytes("GIF89a....")[..3]));                      // too short
        Assert.Null(FileSniffer.Detect([]));
    }

    [Fact]
    public void Html_and_svg_are_text_but_never_trusted_as_anything_else()
    {
        // They are valid UTF-8 text, so they can only ever be stored and served as inert text/plain downloads.
        Assert.Equal("text/plain", FileSniffer.Detect(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"))!.ContentType);
        Assert.Equal("text/plain", FileSniffer.Detect(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"))!.ContentType);
    }

    [Fact]
    public void Binary_data_posing_as_text_is_refused()
    {
        Assert.Null(FileSniffer.Detect([0x41, 0x42, 0x00, 0x43, 0x44]));                 // NUL byte
        Assert.Null(FileSniffer.Detect([0x41, 0x42, 0xFF, 0xFE, 0x43]));                 // invalid UTF-8
        Assert.Null(FileSniffer.Detect([0x41, 0x42, 0x07, 0x43, 0x44]));                 // BEL control character
    }

    [Fact]
    public void Large_text_files_cut_mid_character_at_the_sample_boundary_are_still_text()
    {
        var text = string.Concat(Enumerable.Repeat("م", 40_000)); // 2 bytes each, so 64 KB lands mid-sequence for odd sample sizes
        var bytes = Encoding.UTF8.GetBytes("a" + text); // shifts alignment so the cut splits a character
        Assert.Equal("text/plain", FileSniffer.Detect(bytes)!.ContentType);
    }

    [Fact]
    public void Macro_enabled_and_embedding_office_files_are_refused()
    {
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("[Content_Types].xml", "word/document.xml", "word/vbaProject.bin")));
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("[Content_Types].xml", "xl/workbook.xml", "xl/embeddings/oleObject1.bin")));
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("[Content_Types].xml", "word/document.xml", "word/embeddings/payload.exe")));
    }

    [Fact]
    public void Plain_zips_and_zip_tricks_are_refused()
    {
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("readme.txt")));                                                   // not OOXML
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("word/document.xml")));                                            // no content types
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("[Content_Types].xml", "word/document.xml", "../evil.txt")));     // zip slip
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip("[Content_Types].xml", "word/document.xml", "/abs/evil.txt")));
        Assert.Null(FileSniffer.Detect(SampleFiles.Zip(["[Content_Types].xml", "word/document.xml", .. Enumerable.Range(0, 2100).Select(i => $"p{i}.xml")]))); // entry flood
        Assert.Null(FileSniffer.Detect([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4]));                                           // truncated / corrupt zip
    }
}

public sealed class DocumentNamesTests
{
    [Theory]
    [InlineData("manual.pdf", ".pdf", "manual.pdf")]
    [InlineData("MANUAL.PDF", ".pdf", "MANUAL.PDF")]
    [InlineData("photo.jpeg", ".jpg", "photo.jpeg")]
    [InlineData("report", ".pdf", "report.pdf")]
    [InlineData("invoice.html", ".png", "invoice.html.png")]
    [InlineData("run.exe", ".pdf", "run.exe.pdf")]
    [InlineData("../../etc/passwd", ".txt", "passwd.txt")]
    [InlineData("C:\\temp\\scan.pdf", ".pdf", "scan.pdf")]
    [InlineData("a<b>:c|d?.pdf", ".pdf", "a_b__c_d_.pdf")]
    [InlineData("", ".pdf", "file.pdf")]
    [InlineData("...", ".pdf", "file.pdf")]
    [InlineData("تقرير الصيانة.pdf", ".pdf", "تقرير الصيانة.pdf")]
    public void Names_are_made_safe_and_match_the_detected_type(string original, string extension, string expected) =>
        Assert.Equal(expected, DocumentNames.Sanitize(original, extension));

    [Fact]
    public void Bidirectional_overrides_that_disguise_extensions_are_removed()
    {
        var disguised = "invoice\u202Efdp.exe"; // renders as "invoiceexe.pdf"
        var safe = DocumentNames.Sanitize(disguised, ".pdf");
        Assert.DoesNotContain('\u202E', safe);
        Assert.EndsWith(".pdf", safe);
    }

    [Fact]
    public void Null_and_control_characters_are_handled()
    {
        Assert.Equal("file.pdf", DocumentNames.Sanitize(null, ".pdf"));
        Assert.Equal("ab.pdf", DocumentNames.Sanitize("a\u0000b\r\n.pdf", ".pdf"));
    }

    [Fact]
    public void Long_names_are_cut_but_keep_a_valid_extension()
    {
        var safe = DocumentNames.Sanitize(new string('x', 500) + ".pdf", ".pdf");
        Assert.True(safe.Length <= 200);
        Assert.EndsWith(".pdf", safe);
    }
}
