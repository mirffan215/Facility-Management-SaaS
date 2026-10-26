using Fms.BuildingBlocks.Csv;

namespace Fms.UnitTests;

public sealed class CsvTests
{
    [Fact]
    public void Simple_rows_and_line_endings()
    {
        Assert.Equal([["a", "b"], ["1", "2"]], Csv.Parse("a,b\r\n1,2\r\n"));
        Assert.Equal([["a", "b"], ["1", "2"]], Csv.Parse("a,b\n1,2"));
        Assert.Equal([["a", "b"], ["1", "2"]], Csv.Parse("a,b\r1,2"));
    }

    [Fact]
    public void Quotes_commas_and_line_breaks_inside_cells()
    {
        var rows = Csv.Parse("name,note\n\"Smith, J\",\"said \"\"hi\"\"\"\n\"multi\nline\",x\n");
        Assert.Equal("Smith, J", rows[1][0]);
        Assert.Equal("said \"hi\"", rows[1][1]);
        Assert.Equal("multi\nline", rows[2][0]);
    }

    [Fact]
    public void Byte_order_mark_blank_lines_and_empty_cells_are_handled()
    {
        var rows = Csv.Parse("﻿a,b,c\n\n1,,3\n,,\n");
        Assert.Equal("a", rows[0][0]);
        Assert.Equal(["1", "", "3"], rows[1]);
        Assert.Equal(3, rows.Count); // header, the data row, and the all-comma row ",,"; the blank line is skipped
    }

    [Fact]
    public void Arabic_text_survives() => Assert.Equal("المقر الرئيسي", Csv.Parse("name\nالمقر الرئيسي")[1][0]);

    [Fact]
    public void An_unclosed_quote_is_an_error() => Assert.Throws<FormatException>(() => Csv.Parse("a,b\n\"oops,1\n"));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"x\"", "\"say \"\"x\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Cells_are_quoted_when_needed(string? value, string expected) => Assert.Equal(expected, Csv.Cell(value));

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1")]
    public void Formula_starting_cells_are_defused(string value)
    {
        var cell = Csv.Cell(value);
        Assert.StartsWith("'", cell.TrimStart('"'));
    }

    [Fact]
    public void Written_lines_parse_back_to_the_same_values()
    {
        var values = new[] { "a,b", "say \"x\"", "multi\nline", "", "عربي", "plain" };
        var back = Csv.Parse(Csv.Line(values) + "\n")[0];
        Assert.Equal(values, back);
    }

    [Theory]
    [InlineData("١٢٣", "123")]
    [InlineData("١٢٠٠٫٥", "1200.5")]
    [InlineData("۱۲۳", "123")]
    [InlineData("12.5", "12.5")]
    [InlineData("abc", "abc")]
    public void Digits_are_normalised(string input, string expected) => Assert.Equal(expected, Csv.NormalizeDigits(input));
}
