using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PrivStack.Desktop.Services.EmergencyKit;

/// <summary>
/// Generates a printable Emergency Kit PDF containing the 12-word
/// BIP39 recovery mnemonic for master password recovery.
/// </summary>
public static class EmergencyKitPdfService
{
    static EmergencyKitPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates a PDF emergency kit and saves it to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="words">The 12 mnemonic words.</param>
    /// <param name="workspaceName">Name of the workspace this kit belongs to.</param>
    /// <param name="outputPath">File path to write the PDF.</param>
    public static void Generate(string[] words, string workspaceName, string outputPath)
    {
        var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(50);
                page.MarginVertical(40);
                page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Grey.Darken3));

                page.Header().Column(col =>
                {
                    col.Item().Text("PrivStack Emergency Kit")
                        .FontSize(24).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text($"Workspace: {workspaceName}")
                        .FontSize(12).FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(2).Text($"Generated: {generatedAt}")
                        .FontSize(10).FontColor(Colors.Grey.Medium);

                    col.Item().PaddingTop(12).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    // Warning box
                    col.Item().Background(Colors.Red.Lighten5)
                        .Border(1).BorderColor(Colors.Red.Lighten2)
                        .Padding(12).Column(warning =>
                        {
                            warning.Item().Text("KEEP THIS DOCUMENT SECURE")
                                .Bold().FontSize(13).FontColor(Colors.Red.Darken2);
                            warning.Item().PaddingTop(6).Text(
                                "This document contains your master password recovery key. " +
                                "Anyone with these words can access all your encrypted data. " +
                                "Store this in a secure physical location (e.g., a safe or safety deposit box). " +
                                "Do not store it digitally or share it with anyone.")
                                .FontSize(10).FontColor(Colors.Red.Darken1);
                        });

                    // Recovery words grid (4 columns x 3 rows)
                    col.Item().PaddingTop(24).Text("Your Recovery Words")
                        .FontSize(16).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text(
                        "Write these words down in order. You will need all 12 words to recover your account.")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(16).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });

                        for (var i = 0; i < words.Length && i < 12; i++)
                        {
                            table.Cell()
                                .Padding(4)
                                .Border(1).BorderColor(Colors.Grey.Lighten2)
                                .Background(Colors.Grey.Lighten5)
                                .Padding(8)
                                .Column(cell =>
                                {
                                    cell.Item().Text($"{i + 1}.")
                                        .FontSize(9).FontColor(Colors.Grey.Medium);
                                    cell.Item().Text(words[i])
                                        .FontSize(14).Bold().FontColor(Colors.Black);
                                });
                        }
                    });

                    // Recovery instructions
                    col.Item().PaddingTop(24).Text("How to Recover")
                        .FontSize(14).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(8).Column(steps =>
                    {
                        steps.Item().Text("1. Open PrivStack and go to the unlock screen.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("2. Click \"Forgot Password? Recover with Emergency Kit\".")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("3. Enter all 12 words exactly as shown above, separated by spaces.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("4. Set a new master password when prompted.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("5. Your data will be re-encrypted with the new password. " +
                                  "This Emergency Kit will remain valid for future recoveries.")
                            .FontSize(10);
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("PrivStack Emergency Kit").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span($"  |  Generated {generatedAt}").FontSize(8).FontColor(Colors.Grey.Lighten1);
                });
            });
        }).GeneratePdf(outputPath);
    }
}
