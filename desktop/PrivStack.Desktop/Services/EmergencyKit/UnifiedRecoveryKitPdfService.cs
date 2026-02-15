using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PrivStack.Desktop.Services.EmergencyKit;

/// <summary>
/// Generates a unified Recovery Kit PDF that covers BOTH vault password
/// recovery and cloud-synced workspace data recovery with a single mnemonic.
/// Replaces the separate Emergency Kit and Cloud Recovery Kit PDFs when
/// cloud sync is enabled.
/// </summary>
public static class UnifiedRecoveryKitPdfService
{
    /// <summary>
    /// Generates the unified recovery kit PDF and saves it to <paramref name="outputPath"/>.
    /// </summary>
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
                    col.Item().Text("PrivStack Recovery Kit")
                        .FontSize(24).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text($"Workspace: {workspaceName}")
                        .FontSize(12).FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(2).Text($"Generated: {generatedAt}")
                        .FontSize(10).FontColor(Colors.Grey.Medium);

                    col.Item().PaddingTop(12).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    // Info box
                    col.Item().Background(Colors.Blue.Lighten5)
                        .Border(1).BorderColor(Colors.Blue.Lighten2)
                        .Padding(12).Column(info =>
                        {
                            info.Item().Text("UNIFIED RECOVERY KEY")
                                .Bold().FontSize(13).FontColor(Colors.Blue.Darken2);
                            info.Item().PaddingTop(6).Text(
                                "This document recovers BOTH your vault master password and your " +
                                "cloud-synced workspace data. A single set of 12 words protects " +
                                "all your encrypted data — local and cloud.")
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
                                "Without these recovery words, your encrypted data CANNOT be recovered. " +
                                "Print this document and store it in a secure physical location " +
                                "(e.g., a safe or safety deposit box). " +
                                "Do not store it digitally or share it with anyone. " +
                                "PrivStack cannot recover your data without these words.")
                                .FontSize(10).FontColor(Colors.Red.Darken1);
                        });

                    // Recovery words grid
                    col.Item().PaddingTop(24).Text("Your 12 Recovery Words")
                        .FontSize(16).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(4).Text(
                        "You will need all 12 words in the exact order shown below.")
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

                    // Recovery instructions — vault
                    col.Item().PaddingTop(24).Text("Recover Your Password")
                        .FontSize(14).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(8).Column(steps =>
                    {
                        steps.Item().Text("1. Open PrivStack and go to the unlock screen.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("2. Click \"Forgot Password? Recover with Emergency Kit\".")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("3. Enter all 12 words exactly as shown above.")
                            .FontSize(10);
                        steps.Item().PaddingTop(4)
                            .Text("4. Set a new master password when prompted.")
                            .FontSize(10);
                    });

                    // Recovery instructions — cloud
                    col.Item().PaddingTop(16).Text("Recover Cloud Workspace")
                        .FontSize(14).Bold().FontColor(Colors.Black);

                    col.Item().PaddingTop(8).Column(steps =>
                    {
                        steps.Item().Text(
                            "When you recover your password using the steps above, your cloud " +
                            "workspace data is automatically restored as well. No additional " +
                            "steps are needed — the same 12 words recover everything.")
                            .FontSize(10);
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("PrivStack Recovery Kit").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span($"  |  Generated {generatedAt}").FontSize(8).FontColor(Colors.Grey.Lighten1);
                });
            });
        }).GeneratePdf(outputPath);
    }
}
