using System.Text;

namespace Fms.BuildingBlocks.Csv;

/// <summary>Reads and writes CSV (RFC 4180) for imports and exports. UTF-8, quotes, embedded commas and line breaks.</summary>
public static class Csv
{
    public static IReadOnlyList<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0; // byte-order mark from Excel

        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow() { EndCell(); if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray()); row.Clear(); }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) inQuotes = true;
            else if (c == ',') EndCell();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else cell.Append(c);
        }

        if (inQuotes) throw new FormatException("The file ends inside a quoted value (a quote is not closed).");
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    /// <summary>
    /// One cell, quoted when needed. A cell a spreadsheet would run as a formula (= + - @ or a leading tab/CR) is prefixed with an
    /// apostrophe so opening an export in Excel can never execute attacker-supplied content.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));

    /// <summary>Arabic-Indic and Persian digits to ASCII, and the Arabic decimal separator to '.', so numbers typed on an Arabic keyboard import correctly.</summary>
    public static string NormalizeDigits(string value)
    {
        if (value.All(c => c < '٠')) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                '٫' => '.',
                '٬' => ',',
                _ => c,
            });
        }

        return sb.ToString();
    }
}
