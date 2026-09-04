using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Serilog;
using XISOSharpTester.Logging;
using XISOSharpTester.Models;

namespace XISOSharpTester.Services;

/// <summary>
/// Provides a static method to export test session results as a
/// formatted PDF report using the QuestPDF library.
/// </summary>
public static class PdfExporter
{
    static PdfExporter()
    {
        Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates a landscape A4 PDF report from the specified
    /// <paramref name="session"/> results and writes it to
    /// <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="session">The completed test session results to export.</param>
    /// <param name="xisoSharpVersion">
    /// Optional version string of the extract-xiso executable used,
    /// included in the report header.
    /// </param>
    /// <param name="outputPath">Full file path where the PDF will be written.</param>
    public static void Export(TestSessionResult session, string? xisoSharpVersion, string outputPath)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(outputPath);
            Log.Information("Exporting PDF report to {Path}", outputPath);
            Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.DefaultTextStyle(static x => x.FontSize(9));

                page.Header().Column(header =>
                {
                    header.Item().Text("XISOSharp Tester — Results Report")
                        .Bold().FontSize(16).FontColor(Colors.Blue.Darken3);

                    var genText = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    if (xisoSharpVersion != null)
                    {
                        genText += $"    extract-xiso: {xisoSharpVersion}";
                    }

                    header.Item().Text(genText).FontSize(8).FontColor(Colors.Grey.Medium);

                    header.Item().PaddingTop(5).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"Files: {session.TotalFiles}  |  " +
                                          $"Passed: {session.PassedFiles}  |  " +
                                          $"Failed: {session.FailedFiles}  |  " +
                                          $"Skipped: {session.SkippedFiles}").Bold();
                            c.Item().Text($"SubTests: {session.TotalSubTests} total, " +
                                          $"{session.PassedSubTests} passed, " +
                                          $"{session.FailedSubTests} failed, " +
                                          $"{session.SkippedSubTests} skipped").FontSize(8);
                        });
                        row.ConstantItem(100).Text($"Time: {session.TotalElapsedSeconds:N1}s")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(static cols =>
                    {
                        cols.RelativeColumn(2.5f);
                        cols.RelativeColumn(1.2f);
                        cols.RelativeColumn();
                        cols.RelativeColumn(5f);
                        cols.RelativeColumn();
                        cols.RelativeColumn();
                    });

                    table.Header(static header =>
                    {
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("File").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("Size").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("Time").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("Tests").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("Status").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3)
                            .Padding(3).Text("P/F/S").Bold();
                    });

                    foreach (var file in session.FileResults)
                    {
                        var bgColor = file.AllPassed ? Colors.Green.Lighten5 :
                            file.Failed > 0 ? Colors.Red.Lighten5 : Colors.Grey.Lighten4;

                        var statusText = file.AllPassed ? "PASS" :
                            file.Failed > 0 ? "FAIL" : "SKIP";

                        table.Cell().Background(bgColor).Padding(3)
                            .Text(file.FileName).FontSize(8);
                        table.Cell().Background(bgColor).Padding(3)
                            .Text(file.FileSize).FontSize(8);
                        table.Cell().Background(bgColor).Padding(3)
                            .Text($"{file.ElapsedSeconds:N1}s").FontSize(8);
                        table.Cell().Background(bgColor).Padding(3)
                            .Text(FormatSubTests(file)).FontSize(7);
                        table.Cell().Background(bgColor).Padding(3)
                            .Text(statusText).Bold().FontSize(8)
                            .FontColor(file.AllPassed ? Colors.Green.Darken2 : Colors.Red.Darken2);
                        table.Cell().Background(bgColor).Padding(3)
                            .Text($"{file.Passed}/{file.Failed}/{file.Skipped}").FontSize(8);
                    }
                });

                page.Footer().AlignCenter()
                    .Text("XISOSharp Tester — generated with QuestPDF").FontSize(7)
                    .FontColor(Colors.Grey.Medium);
            });
        }).GeneratePdf(outputPath);
            Log.Information("PDF report written to {Path}", outputPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PDF export failed for {Path}", outputPath);
            BugReporter.ReportException(ex, $"PDF export failed for {outputPath}");
            throw;
        }
    }

    private static string FormatSubTests(PerFileResult file)
    {
        var parts = file.SubTests.Select(static t =>
            $"{t.Status switch
            {
                TestStatus.Passed => "\u2713",
                TestStatus.Failed => "\u2717",
                _ => "\u25CB"
            }} {t.TestName}");
        return string.Join("  ", parts);
    }
}