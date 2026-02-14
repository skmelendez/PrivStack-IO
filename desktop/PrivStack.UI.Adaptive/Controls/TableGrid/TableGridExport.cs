// ============================================================================
// File: TableGridExport.cs
// Description: Export utilities for the TableGrid. Converts TableGridData to
//              CSV, TSV, Markdown, and JSON formats. Supports file save dialog
//              and clipboard copy.
// ============================================================================

using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

public static class TableGridExport
{
    public static async Task ExportToFileAsync(TableGridData data, string format, TopLevel topLevel)
    {
        var (content, extension) = FormatData(data, format);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export Table as {extension.ToUpperInvariant()}",
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType($"{extension.ToUpperInvariant()} Files")
                {
                    Patterns = [$"*.{extension}"]
                }
            ]
        });

        if (file == null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    public static async Task CopyToClipboardAsync(TableGridData data, string format, TopLevel topLevel)
    {
        var clipboard = topLevel.Clipboard;
        if (clipboard == null) return;

        var (content, _) = FormatData(data, format);
        await clipboard.SetTextAsync(content);
    }

    private static (string content, string extension) FormatData(TableGridData data, string format) =>
        format switch
        {
            "csv" => (ToCsv(data, ','), "csv"),
            "tsv" => (ToCsv(data, '\t'), "tsv"),
            "md" => (ToMarkdown(data), "md"),
            "json" => (ToJson(data), "json"),
            _ => (ToCsv(data, ','), "csv")
        };

    internal static string ToCsv(TableGridData data, char delimiter)
    {
        var sb = new StringBuilder();

        foreach (var row in data.HeaderRows)
        {
            AppendCsvRow(sb, row.Cells, delimiter);
        }

        foreach (var row in data.DataRows)
        {
            AppendCsvRow(sb, row.Cells, delimiter);
        }

        return sb.ToString();
    }

    private static void AppendCsvRow(StringBuilder sb, IReadOnlyList<string> cells, char delimiter)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var cell = cells[i];
            if (cell.Contains(delimiter) || cell.Contains('"') || cell.Contains('\n'))
            {
                sb.Append('"');
                sb.Append(cell.Replace("\"", "\"\""));
                sb.Append('"');
            }
            else
            {
                sb.Append(cell);
            }
        }
        sb.AppendLine();
    }

    internal static string ToMarkdown(TableGridData data)
    {
        var sb = new StringBuilder();

        if (data.HeaderRows.Count > 0)
        {
            var header = data.HeaderRows[0];
            sb.Append('|');
            foreach (var cell in header.Cells)
            {
                sb.Append(' ');
                sb.Append(cell);
                sb.Append(" |");
            }
            sb.AppendLine();

            // Separator
            sb.Append('|');
            for (var c = 0; c < header.Cells.Count; c++)
            {
                var alignment = c < data.Columns.Count ? data.Columns[c].Alignment : TableColumnAlignment.Left;
                sb.Append(alignment switch
                {
                    TableColumnAlignment.Center => " :---: |",
                    TableColumnAlignment.Right => " ---: |",
                    _ => " --- |"
                });
            }
            sb.AppendLine();
        }

        foreach (var row in data.DataRows)
        {
            sb.Append('|');
            foreach (var cell in row.Cells)
            {
                sb.Append(' ');
                sb.Append(cell);
                sb.Append(" |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string ToJson(TableGridData data)
    {
        var rows = new List<Dictionary<string, string>>();
        var colNames = data.Columns.Select(c => c.Name).ToList();

        foreach (var row in data.DataRows)
        {
            var dict = new Dictionary<string, string>();
            for (var c = 0; c < Math.Min(colNames.Count, row.Cells.Count); c++)
            {
                dict[colNames[c]] = row.Cells[c];
            }
            rows.Add(dict);
        }

        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }
}
