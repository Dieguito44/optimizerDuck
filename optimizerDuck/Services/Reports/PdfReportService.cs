using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Optimization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace optimizerDuck.Services.Reports;

/// <summary>Draws the client PDF (plan phase 3) with QuestPDF from a <see cref="SessionReportModel" />.</summary>
public sealed class PdfReportService(
    OptimizationRegistry optimizations,
    TimeProvider timeProvider,
    ILogger<PdfReportService> logger
)
{
    private const string Accent = "#0F7B8A";

    static PdfReportService()
    {
        // QuestPDF Community license (plan section 3). See the licensing note in the plan.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>Builds the report model for a session and writes the PDF next to the session.</summary>
    /// <returns>The PDF path.</returns>
    public async Task<string> GenerateAsync(ServiceSession session, CancellationToken ct = default)
    {
        var model = await BuildModelAsync(session).ConfigureAwait(false);

        var folder = SessionStore.FolderFor(session);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(
            folder,
            $"Informe_Zentra_{timeProvider.GetLocalNow():yyyy-MM-dd_HHmm}.pdf"
        );
        // Render to a temporary file first: a failed render must not leave an empty PDF behind.
        var temp = path + ".tmp";
        try
        {
            await Task.Run(() => Render(model, temp), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        logger.LogInformation("Report PDF written to {Path}", path);
        return path;
    }

    /// <summary>The report model for a session: the PDF draws it, the AI report is built from it.</summary>
    public async Task<SessionReportModel> BuildModelAsync(ServiceSession session)
    {
        await optimizations.EnsurePreloadedAsync().ConfigureAwait(false);
        var names = optimizations
            .OptimizationCategories.SelectMany(c => c.Optimizations)
            .GroupBy(o => o.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);

        return SessionReportModel.Build(
            session,
            ReadHistory(),
            id => names.TryGetValue(id, out var name) ? name : null,
            timeProvider.GetUtcNow(),
            Loc.CurrentCulture
        );
    }

    private IEnumerable<ChangeRecord> ReadHistory()
    {
        if (!Directory.Exists(Shared.HistoryDirectory))
            return [];
        var records = new List<ChangeRecord>();
        foreach (var file in Directory.EnumerateFiles(Shared.HistoryDirectory, "*.json"))
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
                if (History.ChangeRecordStore.TryRead(id, logger) is { } record)
                    records.Add(record);
        return records;
    }

    /// <summary>Writes the PDF. Public so a test can render a model without a session.</summary>
    public static void Render(SessionReportModel model, string path)
    {
        Document
            .Create(document =>
                document.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(40);
                    // Lato ships with QuestPDF (OFL). Since 2026.9 system fonts are not used, so
                    // the PDF looks the same on every PC.
                    page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Lato"));

                    page.Header().Element(c => Header(c, model));
                    page.Content().PaddingVertical(12).Column(column => Content(column, model));
                    page.Footer()
                        .AlignCenter()
                        .Text(text =>
                        {
                            text.DefaultTextStyle(t =>
                                t.FontSize(8).FontColor(Colors.Grey.Darken1)
                            );
                            text.Span(Loc.Instance["Report.Footer"] + "   ");
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                })
            )
            .GeneratePdf(path);
    }

    private static void Header(IContainer container, SessionReportModel model)
    {
        container.Row(row =>
        {
            row.RelativeItem()
                .Column(column =>
                {
                    column.Item().Text(model.Title).FontSize(18).Bold().FontColor(Accent);
                    if (model.ClientLabel is { } client)
                        column.Item().Text(Loc.Instance["Report.Client", client]);
                    column.Item().Text(model.Date).FontColor(Colors.Grey.Darken1);
                });
            row.ConstantItem(170)
                .AlignRight()
                .Column(column =>
                {
                    column.Item().AlignRight().Text(Loc.Instance["Report.Score"]).FontSize(9);
                    column
                        .Item()
                        .AlignRight()
                        .Text(text =>
                        {
                            text.Span(model.ScoreBefore?.ToString() ?? "—").FontSize(26).Bold();
                            text.Span("  »  ").FontSize(16).FontColor(Colors.Grey.Medium);
                            text.Span(model.ScoreAfter?.ToString() ?? "—")
                                .FontSize(26)
                                .Bold()
                                .FontColor(Accent);
                        });
                    column
                        .Item()
                        .AlignRight()
                        .Text(Loc.Instance["Report.ScoreLegend"])
                        .FontSize(8)
                        .FontColor(Colors.Grey.Darken1);
                });
        });
    }

    private static void Content(ColumnDescriptor column, SessionReportModel model)
    {
        column.Spacing(10);

        if (model.Written is { } written)
        {
            Section(column, "Report.Section.Summary");
            column.Item().Text(written.Summary);
            if (written.Priorities.Count > 0)
            {
                Section(column, "Report.Section.Priorities");
                foreach (var p in written.Priorities)
                    column
                        .Item()
                        .Column(item =>
                        {
                            item.Item()
                                .Text(text =>
                                {
                                    text.Span($"{p.Number}. {p.Title}").Bold();
                                    text.Span($"   {p.Who}").FontColor(Colors.Grey.Darken1);
                                });
                            item.Item().PaddingLeft(14).Text(p.Explanation);
                        });
            }
            foreach (var warning in written.Warnings)
                column.Item().Text($"• {warning}").FontColor(Colors.Orange.Darken3);
            column.Item().Text(written.Source).FontSize(8).FontColor(Colors.Grey.Darken1);
        }

        Section(column, "Report.Section.Metrics");
        column
            .Item()
            .Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.RelativeColumn(2);
                    c.RelativeColumn(2);
                    c.RelativeColumn(2);
                });
                table.Header(h =>
                {
                    foreach (
                        var key in new[]
                        {
                            "Report.Col.Metric",
                            "Report.Col.Before",
                            "Report.Col.After",
                            "Report.Col.Change",
                        }
                    )
                        h.Cell()
                            .Background(Colors.Grey.Lighten3)
                            .Padding(4)
                            .Text(Loc.Instance[key])
                            .Bold();
                });
                foreach (var row in model.Metrics)
                {
                    table
                        .Cell()
                        .BorderBottom(0.5f)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4)
                        .Text(row.Name);
                    table
                        .Cell()
                        .BorderBottom(0.5f)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4)
                        .Text(row.Before);
                    table
                        .Cell()
                        .BorderBottom(0.5f)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4)
                        .Text(row.After);
                    var color = row.Improved switch
                    {
                        true => Colors.Green.Darken2,
                        false => Colors.Red.Darken2,
                        _ => Colors.Grey.Darken2,
                    };
                    table
                        .Cell()
                        .BorderBottom(0.5f)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4)
                        .Text(row.Difference)
                        .FontColor(color);
                }
            });

        Section(column, "Report.Section.Findings");
        if (model.Findings.Count == 0)
            column.Item().Text(Loc.Instance["Report.NoFindings"]);
        foreach (var finding in model.Findings)
        {
            column
                .Item()
                .BorderLeft(3)
                .BorderColor(Accent)
                .PaddingLeft(8)
                .Column(f =>
                {
                    f.Item()
                        .Text(text =>
                        {
                            text.Span(finding.Title).Bold();
                            text.Span($"   {finding.Severity}").FontColor(Colors.Grey.Darken1);
                            if (finding.Points > 0)
                                text.Span($"   -{finding.Points}").FontColor(Colors.Red.Darken2);
                        });
                    f.Item().Text(finding.Description);
                    foreach (var line in finding.Evidence)
                        f.Item().Text(line).FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                });
        }

        Section(column, "Report.Section.Applied");
        if (model.Applied.Count == 0)
            column.Item().Text(Loc.Instance["Report.NoApplied"]);
        foreach (var action in model.Applied)
            column.Item().Text($"• {action.Name}");

        if (model.ClientPending.Count > 0)
        {
            Section(column, "Report.Section.Pending");
            foreach (var item in model.ClientPending)
                column.Item().Text($"• {item}");
        }

        if (model.BootCulprits.Count > 0)
        {
            Section(column, "Report.Section.BootCulprits");
            foreach (var culprit in model.BootCulprits)
                column.Item().Text($"• {culprit.Name} (+{culprit.DelayMs / 1000.0:0.0} s)");
        }

        if (model.NotMeasured.Count > 0)
        {
            Section(column, "Report.Section.NotMeasured");
            foreach (var item in model.NotMeasured)
                column.Item().Text($"• {item}").FontSize(9);
        }

        Section(column, "Report.Section.Notes");
        foreach (var note in model.Notes)
            column.Item().Text(note).FontSize(9);
    }

    private static void Section(ColumnDescriptor column, string key)
    {
        column.Item().PaddingTop(6).Text(Loc.Instance[key]).FontSize(13).Bold().FontColor(Accent);
    }
}
