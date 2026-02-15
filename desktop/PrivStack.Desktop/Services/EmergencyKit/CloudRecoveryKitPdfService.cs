using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PrivStack.Desktop.Services.EmergencyKit;

/// <summary>
/// Generates a printable Cloud Workspace Recovery Kit PDF containing
/// the 12-word BIP39 mnemonic for recovering cloud-encrypted workspace data.
/// </summary>
public static class CloudRecoveryKitPdfService
{
    /// <summary>
    /// Generates the recovery kit PDF and saves it to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="words">The 12 mnemonic recovery words.</param>
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
                    col.Item().Text("PrivStack Cloud Workspace Recovery Kit")
                        .FontSize(22).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text($"Workspace: {workspaceName}")
                        .FontSize(12).FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(2).Text($"Generated: {generatedAt}")
                        .FontSize(10).FontColor(Colors.Grey.Medium);

                    col.Item().PaddingTop(12).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    // What this is
                    col.Item().Background(Colors.Blue.Lighten5)
                        .Border(1).BorderColor(Colors.Blue.Lighten2)
                        .Padding(12).Column(info =>
                        {
                            info.Item().Text("WHAT IS THIS?")
                                .Bold().FontSize(13).FontColor(Colors.Blue.Darken2);
                            info.Item().PaddingTop(6).Text(
                                "This document contains the recovery key for your cloud-synced workspace. " +
                                "If you lose your master password or need to restore your encrypted cloud data " +
                                "on a new device, these 12 words are the ONLY way to recover your workspace data.")
                                .FontSize(10).FontColor(Colors.Blue.Darken1);
                        });

                    // Warning box
                    col.Item().PaddingTop(12).Background(Colors.Red.Lighten5)
                        .Border(1).BorderColor(Colors.Red.Lighten2)
                        .Padding(12).Column(warning =>
                        {
                            warning.Item().Text("DO NOT LOSE THIS DOCUMENT")
                                .Bold().FontSize(13).FontColor(Colors.Red.Darken2);
                            warning.Item().PaddingTop(6).Text(
                                "Without these recovery words, your cloud-encrypted workspace data CANNOT be recovered. " +
                                "Print this document and store it in a secure physical location (e.g., a safe or safety deposit box). " +
                                "Do not store it digitally or share it with anyone. " +
                                "PrivStack cannot recover your data without these words.")
                                .FontSize(10).FontColor(Colors.Red.Darken1);
                        });

                    // Recovery words grid (4 columns x 3 rows)
                    col.Item().PaddingTop(24).Text("Your 12 Recovery Words")
                        .FontSize(16).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text(
                        "You will need all 12 words in the exact order shown below to recover your workspace.")
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
                    col.Item().PaddingTop(24).Text("How to Recover Your Workspace")
                        .FontSize(14).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(8).Column(steps =>
                    {
                        steps.Item().Text("1. Open PrivStack and connect to PrivStack Cloud in Settings.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("2. In the Cloud Sync section, click \"Recover encryption key\".")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("3. Enter all 12 words exactly as shown above, separated by spaces.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("4. Your cloud-encrypted workspace data will be accessible again.")
                            .FontSize(10);
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("PrivStack Cloud Recovery Kit").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span($"  |  Generated {generatedAt}").FontSize(8).FontColor(Colors.Grey.Lighten1);
                });
            });
        }).GeneratePdf(outputPath);
    }
}
