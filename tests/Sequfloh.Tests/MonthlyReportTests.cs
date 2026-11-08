using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

public sealed class MonthlyReportCalculationTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Liability_ratio_failure_time_and_fai_total_follow_the_formulas()
    {
        Assert.Equal(750m, MonthlyReportMath.Liability(10, 75m));
        Assert.Equal(900m, MonthlyReportMath.Liability(12, 75m));
        Assert.Null(MonthlyReportMath.Liability(null, 75m));
        Assert.Equal("4:1", MonthlyReportMath.Ratio(200m, 50m));
        Assert.Equal("2.5:1", MonthlyReportMath.Ratio(10m, 4m));
        Assert.Equal(MonthlyReportMath.EmptyNote, MonthlyReportMath.Ratio(10m, 0m));
        Assert.Equal(MonthlyReportMath.EmptyNote, MonthlyReportMath.Ratio(null, 8m));
        var (mean, median) = MonthlyReportMath.TimeToFailure([1m, 9m, 2m]);
        Assert.Equal(4.0m, mean);
        Assert.Equal(2m, median);
        var even = MonthlyReportMath.TimeToFailure([1m, 2m, 3m, 4m]);
        Assert.Equal(2.5m, even.Median);
        Assert.Null(MonthlyReportMath.TimeToFailure([]).Mean);
        var total = MonthlyReportMath.FaiTotal([
            new FaiRow { Category = "Pumps", TotalCompleted = 3, Passed = 1, Failed = 1, PassedWithDeviation = 1 },
            new FaiRow { Category = "Struts", TotalCompleted = 2, Passed = 2, Failed = 0, PassedWithDeviation = 0 },
        ]);
        Assert.Equal(5, total.TotalCompleted);
        Assert.Equal(3, total.Passed);
        Assert.Equal(1, total.Failed);
        Assert.Equal(1, total.PassedWithDeviation);
    }

    [Fact]
    public void Trend_uses_saved_months_and_calculates_the_current_ratio()
    {
        var august = new MonthlyReportBody { LaborAmountRequested = 40m, PartsAmountRequested = 10m, AlertsTotal = 3, MttfDays = 10.5m, TotalClaimsRaw = 8 };
        var september = new MonthlyReportBody { LaborAmountRequested = 9m, PartsAmountRequested = 3m, AlertsTotal = 1 };
        var trend = MonthlyReportMath.Trend(2026, 9, september, [(2026, 8, august)]);
        Assert.Equal(6, trend.Count);
        Assert.Equal(MonthlyReportMath.EmptyNote, trend[0].Ratio);
        Assert.False(trend[0].HasReport);
        var aug = trend.Single(t => t.Month == 8);
        Assert.Equal("4:1", aug.Ratio);
        Assert.Equal(8, aug.TotalClaims);
        Assert.Equal("10.5 Days", aug.Mttf);
        var sep = trend.Single(t => t.Current);
        Assert.Equal("3:1", sep.Ratio);
        Assert.Null(sep.TotalClaims);
        Assert.Equal(1, sep.ProductAlerts);
        Assert.Equal(MonthlyReportMath.EmptyNote, sep.Mttf);
    }

    [Fact]
    public void The_printed_report_keeps_the_same_headings_in_order()
    {
        var body = new MonthlyReportBody { DepartmentStatus = "Yellow", ApprovedByName = "Pat Example", DocumentDate = "2026-09-30" };
        var blocks = MonthlyReportLayout.Build(2026, 9, body, 75m, MonthlyReportMath.Trend(2026, 9, body, []));
        var identity = MonthlyReportIdentity.From(body, 2026, 9);
        var headings = blocks.OfType<ReportHeading>().Select(h => h.Text).ToList();
        Assert.Equal(
        [
            MonthlyReportLayout.H61, MonthlyReportLayout.HFin, MonthlyReportLayout.HPayout("September"), MonthlyReportLayout.H62, MonthlyReportLayout.HTop,
            MonthlyReportLayout.H622, MonthlyReportLayout.HVeh, MonthlyReportLayout.H623, MonthlyReportLayout.H624, MonthlyReportLayout.H63, MonthlyReportLayout.H64,
            MonthlyReportLayout.H65, MonthlyReportLayout.H66, MonthlyReportLayout.H67, MonthlyReportLayout.H68, MonthlyReportLayout.H69, MonthlyReportLayout.H610,
            MonthlyReportLayout.H611, MonthlyReportLayout.H612, MonthlyReportLayout.H613,
        ], headings);
        Assert.Equal("Monthly Quality Report - September 2026", blocks.OfType<ReportTitle>().Single().Text);
        Assert.Contains(blocks.OfType<ReportTable>(), t => t.Headers[0] == "Part Number" && t.Headers[2] == "Total Claims" && t.Rows[0][0] == MonthlyReportMath.EmptyNote);
        Assert.Contains(blocks.OfType<ReportTable>(), t => t.Headers.Contains("Passed w/ Deviation") && t.Rows[0][0] == MonthlyReportMath.EmptyNote);
        Assert.DoesNotContain(blocks.OfType<ReportBullets>().SelectMany(b => b.Items), t => t.Contains("PIRs Logged", StringComparison.Ordinal));
        var withFai = MonthlyReportLayout.Build(2026, 9, new MonthlyReportBody { FirstArticle = [new FaiRow { Category = "Pumps", TotalCompleted = 1, Passed = 1 }] }, 75m, MonthlyReportMath.Trend(2026, 9, body, []));
        Assert.Equal("Total", withFai.OfType<ReportTable>().Single(t => t.Headers.Contains("Passed w/ Deviation")).Rows[^1][0]);
        Assert.Empty(blocks.OfType<ReportPicture>());
        var pdf = MonthlyReportExport.Pdf(identity, blocks, new Dictionary<string, byte[]>());
        var docx = MonthlyReportExport.Docx(identity, blocks, new Dictionary<string, byte[]>());
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..5]));
        Assert.Contains("/Type /Page", System.Text.Encoding.ASCII.GetString(pdf));
        using var zip = new ZipArchive(new MemoryStream(docx));
        var xml = Read(zip, "word/document.xml");
        var header = Read(zip, "word/header1.xml");
        var styles = Read(zip, "word/styles.xml");
        Assert.Contains("6.1 Executive Summary", xml);
        Assert.Contains("w:highlight w:val=\"yellow\"", xml);
        Assert.Contains("TMP-ENG-001: Monthly Engineering Development Report", header);
        Assert.Contains("Approved By: Pat Example", header);
        Assert.Contains("9/30/2026", header);
        Assert.Contains("PAGE", header);
        Assert.Contains("NUMPAGES", header);
        Assert.Contains("Aptos Display", styles);
        Assert.Contains("0F4761", styles);
        Assert.Contains("6.13", xml);
        Assert.Contains(MonthlyReportMath.EmptyNote, xml);
        Assert.DoesNotContain("PIRs Logged", xml);
        Assert.DoesNotContain("N/A", xml);
        Assert.DoesNotContain("a:blip", xml);
    }

    [Fact]
    public void Labor_claims_fixture_counts_only_Sheet1_rows_in_the_month()
    {
        using var stream = File.OpenRead(Fixture("labor-claims-fake.xlsx"));
        var result = new LaborClaimsParser().Read(stream, 2026, 9);
        Assert.Equal(1, result.IncludedRows);
        Assert.Equal(1, result.DistinctCount);
        Assert.Equal("1", result.Fields[MonthlyReportFields.TotalClaimsProcessed]);
        Assert.Equal("1", result.Fields[MonthlyReportFields.ClaimsApproved]);
        Assert.Equal("0", result.Fields[MonthlyReportFields.ClaimsDenied]);
        Assert.False(result.Fields.ContainsKey(MonthlyReportFields.DmaParts));
        Assert.Equal("1", result.Fields[MonthlyReportFields.TotalClaimsRaw]);
        Assert.Equal("125", result.Fields[MonthlyReportFields.TotalAmountRequested]);
        Assert.Equal("125", result.Fields[MonthlyReportFields.PartsAmountRequested]);
        Assert.Equal("125", result.Fields[MonthlyReportFields.LaborAmountRequested]);
        Assert.Equal("Sheet11", result.FieldTabs[MonthlyReportFields.TotalClaimsRaw]);
        Assert.Equal("FAKE-001", result.TopParts.Single().PartNumber);
        Assert.Equal(1, result.TopParts.Single().TotalClaims);
        Assert.Contains(result.Warnings, w => w.Contains("Received", StringComparison.Ordinal) && w.Contains("not added", StringComparison.Ordinal));
        var headers = result.Tabs.Single(t => t.Tab == "Sheet1").Headers;
        Assert.Equal("", headers[0]);
        Assert.Contains(headers, h => h == "Amount DMA authorized ");
        Assert.Contains(result.Warnings, w => w.Contains("NAPA Black Label Warranty", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Sheet12", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("another month", StringComparison.Ordinal));
        Assert.Equal(0, result.SkippedTotal);
    }

    [Fact]
    public void Product_alert_fixture_uses_the_Oreilly_tab_only()
    {
        using var stream = File.OpenRead(Fixture("product-alerts-fake.xlsx"));
        var result = new ProductAlertsParser().Read(stream, 2026, 9);
        Assert.Equal("1", result.Fields[MonthlyReportFields.AlertsTotal]);
        Assert.Equal("0", result.Fields[MonthlyReportFields.AlertsOpen]);
        Assert.Equal("2", result.Fields[MonthlyReportFields.HoursInvestigating]);
        Assert.Equal("2", result.Fields[MonthlyReportFields.AvgDaysToClose]);
        Assert.Contains(result.Warnings, w => w.Contains("Sheet4", StringComparison.Ordinal));
        Assert.Equal(0, result.SkippedTotal);
        var headers = result.Tabs.Single().Headers;
        Assert.Contains(headers, h => h == "Engineer Responsible ");
    }

    [Fact]
    public void Fai_fixture_counts_first_completions_and_keeps_duplicate_notes_columns()
    {
        using var stream = File.OpenRead(Fixture("engineering-fai-fake.xlsx"));
        var result = new EngineeringFaiParser().Read(stream, 2026, 9);
        Assert.Equal(3, result.IncludedRows);
        var rows = result.FirstArticle.ToDictionary(r => r.Category);
        Assert.Equal(1, rows["CSAs & Shocks"].Passed);
        Assert.Equal(1, rows["CSAs & Shocks"].TotalCompleted);
        Assert.Equal(1, rows["Fuel & Lift Supports"].Passed);
        Assert.Equal(1, rows["Fuel Injector"].TotalCompleted);
        Assert.Contains(result.Warnings, w => w.Contains("not in the reviewed category list", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("second FAI", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("OR Injector", StringComparison.Ordinal));
        var sammy = result.Tabs.Single(t => t.Tab == "Sammy G's tab");
        Assert.Equal(2, sammy.Headers.Count(h => MonthlySourceRead.Norm(h) == "notes"));
        var leeroy = result.Tabs.Single(t => t.Tab == "Leeroy's Tab");
        Assert.Contains(leeroy.Headers, h => h == "Notes ");
    }

    [Fact]
    public void Bad_rows_are_named_and_unknown_words_are_not_forced_into_a_bucket()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Sheet1");
        sheet.Cell(2, 4).Value = "claim#";
        sheet.Cell(2, 15).Value = "Conclusion";
        sheet.Cell(2, 6).Value = "Part#";
        sheet.Cell(3, 1).Value = "/";
        sheet.Cell(3, 4).Value = "C-1";
        sheet.Cell(4, 1).Value = "9/2/2026";
        sheet.Cell(4, 4).Value = "";
        sheet.Cell(5, 1).Value = "9/3/2026";
        sheet.Cell(5, 4).Value = "C-2";
        sheet.Cell(5, 15).Value = "Approved 1";
        sheet.Cell(6, 1).Value = "9/4/2026";
        sheet.Cell(6, 4).Value = "C-2";
        sheet.Cell(6, 15).Value = "accepted";
        sheet.Cell(7, 1).Value = "9/5/2026";
        sheet.Cell(7, 4).Value = "C-3";
        sheet.Cell(7, 15).Value = "N/A";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        var result = new LaborClaimsParser().Read(ms, 2026, 9);
        Assert.Equal(3, result.IncludedRows);
        Assert.Equal(2, result.DistinctCount);
        Assert.Equal("1", result.Fields[MonthlyReportFields.ClaimsApproved]);
        Assert.Equal("0", result.Fields[MonthlyReportFields.ClaimsDenied]);
        Assert.Contains(result.Skipped, s => s.Row == 3 && s.Reason.Contains('/'));
        Assert.Contains(result.Skipped, s => s.Row == 4 && s.Reason.Contains("claim#"));
        Assert.Contains(result.Warnings, w => w.Contains("Approved 1", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("distinct claim", StringComparison.Ordinal));
        Assert.Equal(-1250.50m, MonthlySourceRead.ParseMoney("($1,250.50)", out _));
        Assert.Null(MonthlySourceRead.ParseMoney("/", out _));
        Assert.Null(MonthlySourceRead.ParseUsDate("N/A", out _));
        Assert.Equal(new DateOnly(2026, 9, 15), MonthlySourceRead.ParseUsDate("9/15/26", out _));
    }

    [Fact]
    public void First_fai_disposition_is_not_taken_from_the_engineering_column_or_the_second_fai()
    {
        using var wb = new XLWorkbook();
        var leeroy = wb.AddWorksheet("Leeroy's Tab");
        leeroy.Cell(3, 1).Value = "Part #";
        leeroy.Cell(3, 2).Value = "Drawing";
        leeroy.Cell(3, 5).Value = "completed date";
        leeroy.Cell(3, 7).Value = "Disposition";
        leeroy.Cell(3, 10).Value = "Engineering Disposition";
        leeroy.Cell(3, 14).Value = "2nd FAI Completion date";
        leeroy.Cell(4, 1).Value = "P-1";
        leeroy.Cell(4, 5).Value = "10/2/2026";
        leeroy.Cell(4, 7).Value = "Fail";
        leeroy.Cell(4, 10).Value = "Passed";
        leeroy.Cell(4, 14).Value = "9/20/2026";
        leeroy.Cell(5, 1).Value = "P-2";
        leeroy.Cell(5, 5).Value = "9/8/2026";
        leeroy.Cell(5, 7).Value = "Needs Special Attention";
        leeroy.Cell(5, 10).Value = "Failed";
        var fuel = wb.AddWorksheet("Fuel");
        fuel.Cell(1, 5).Value = "Part Type";
        fuel.Cell(1, 19).Value = "1st FAI Disposition";
        fuel.Cell(1, 20).Value = "1st FAI Completion Date";
        fuel.Cell(2, 5).Value = "Mystery Widget";
        fuel.Cell(2, 19).Value = "Pass with Deviation";
        fuel.Cell(2, 20).Value = "9/9/2026";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        var result = new EngineeringFaiParser().Read(ms, 2026, 9);
        var shocks = result.FirstArticle.Single(r => r.Category == "CSAs & Shocks");
        Assert.Equal(1, shocks.TotalCompleted);
        Assert.Equal(0, shocks.Passed);
        Assert.Equal(0, shocks.Failed);
        var mystery = result.FirstArticle.Single(r => r.Category == "Mystery Widget");
        Assert.Equal(1, mystery.PassedWithDeviation);
        Assert.Contains(result.Warnings, w => w.Contains("second FAI", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Needs Special Attention", StringComparison.Ordinal));
    }

    static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}

[Collection("api")]
public sealed class MonthlyReportApiTests(TestApp app)
{
    [Fact]
    public async Task Permissions_gate_view_edit_and_finalize()
    {
        var view = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohMonthlyReportsView]);
        var edit = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohMonthlyReportsView, Permissions.SequflohMonthlyReportsEdit]);
        var final = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohMonthlyReportsView, Permissions.SequflohMonthlyReportsEdit, Permissions.SequflohMonthlyReportsFinalize]);
        var none = await app.CreateUserAsync([Permissions.SequflohAccess]);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Client.GetAsync("/api/sequfloh/monthly-reports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await view.Client.GetAsync("/api/sequfloh/monthly-reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.Client.PostAsync("/api/sequfloh/monthly-reports", new { year = 2031, month = 3, copyText = false })).StatusCode);
        var created = await (await edit.Client.PostOkAsync("/api/sequfloh/monthly-reports", new { year = 2031, month = 4, copyText = false })).JsonAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await edit.Client.PostAsync($"/api/sequfloh/monthly-reports/{created.Id()}/finalize")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await final.Client.PostAsync($"/api/sequfloh/monthly-reports/{created.Id()}/finalize")).StatusCode);
        var admin = await app.AdminAsync();
        var me = await admin.GetElAsync("/api/sequfloh/context");
        var perms = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();
        Assert.Contains(Permissions.SequflohMonthlyReportsView, perms);
        Assert.Contains(Permissions.SequflohMonthlyReportsEdit, perms);
        Assert.Contains(Permissions.SequflohMonthlyReportsFinalize, perms);
    }

    [Fact]
    public async Task Import_fills_editable_fields_and_an_override_is_kept_and_audited()
    {
        var admin = await app.AdminAsync();
        var created = await (await admin.PostOkAsync("/api/sequfloh/monthly-reports", new { year = 2026, month = 9, copyText = false })).JsonAsync();
        var id = created.Id();
        await Upload(admin, id, "labor-claims", "labor-claims-fake.xlsx");
        await Upload(admin, id, "product-alerts", "product-alerts-fake.xlsx");
        await Upload(admin, id, "engineering-fai", "engineering-fai-fake.xlsx");
        await Upload(admin, id, "oreilly-raw", "labor-claims-fake.xlsx");
        await Upload(admin, id, "ncr-log", "product-alerts-fake.xlsx");
        var detail = await admin.GetElAsync($"/api/sequfloh/monthly-reports/{id}");
        var body = detail.GetProperty("body");
        Assert.Equal(1, body.GetProperty("totalClaimsProcessed").GetInt32());
        Assert.Equal(1, body.GetProperty("claimsApproved").GetInt32());
        Assert.Equal(0, body.GetProperty("claimsDenied").GetInt32());
        Assert.Equal(1, body.GetProperty("totalClaimsRaw").GetInt32());
        Assert.Equal(125m, body.GetProperty("laborAmountRequested").GetDecimal());
        Assert.Equal(1, body.GetProperty("alertsTotal").GetInt32());
        Assert.Equal(3, body.GetProperty("firstArticle").GetArrayLength());
        Assert.False(body.GetProperty("origin").GetProperty("totalClaimsProcessed").GetProperty("overridden").GetBoolean());
        Assert.Contains("labor-claims-fake.xlsx", body.GetProperty("origin").GetProperty("totalClaimsProcessed").GetProperty("fileName").GetString());
        var rawNote = detail.GetProperty("files").EnumerateArray().First(f => f.GetProperty("fileType").GetString() == "oreilly-raw");
        Assert.True(rawNote.GetProperty("result").GetProperty("applied").GetBoolean());

        body = (await (await admin.PutOkAsync($"/api/sequfloh/monthly-reports/{id}", new
        {
            flatRate = 80m,
            rowVersion = detail.GetProperty("rowVersion").GetUInt32(),
            body = new
            {
                departmentStatus = "Yellow",
                primaryAchievement = "Cleared the made-up queue.",
                criticalRisks = new[] { "A made-up blocker." },
                totalClaimsProcessed = 9,
                claimsApproved = 1,
                claimsDenied = 0,
                alertsTotal = 1,
                alertsOpen = 0,
                hoursInvestigating = 2m,
                avgDaysToClose = 2m,
                firstArticle = body.GetProperty("firstArticle"),
            },
        })).JsonAsync()).GetProperty("body");
        Assert.Equal(9, body.GetProperty("totalClaimsProcessed").GetInt32());
        Assert.True(body.GetProperty("origin").GetProperty("totalClaimsProcessed").GetProperty("overridden").GetBoolean());

        await Upload(admin, id, "labor-claims", "labor-claims-fake.xlsx");
        detail = await admin.GetElAsync($"/api/sequfloh/monthly-reports/{id}");
        Assert.Equal(9, detail.GetProperty("body").GetProperty("totalClaimsProcessed").GetInt32());
        Assert.Equal("1", detail.GetProperty("body").GetProperty("origin").GetProperty("totalClaimsProcessed").GetProperty("computed").GetString());

        var audit = await admin.GetElAsync($"/api/audit-trail?entityType=MonthlyQualityReport&entityId={id}&pageSize=100");
        var descriptions = audit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(descriptions, d => d.Contains("Created Monthly Quality Report - September 2026", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("Changed tracker processed claims", StringComparison.Ordinal) && d.Contains("from 1", StringComparison.Ordinal) && d.Contains("to 9", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("Edited section 6.1 Executive Summary", StringComparison.Ordinal));
        Assert.All(audit.GetProperty("items").EnumerateArray(), i => Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("who").GetString())));

        var pdf = await admin.GetAsync($"/api/sequfloh/monthly-reports/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var pdfBytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdfBytes[..5]));
        Assert.Contains("/Type /Page", System.Text.Encoding.ASCII.GetString(pdfBytes));
        var docx = await admin.GetAsync($"/api/sequfloh/monthly-reports/{id}/docx");
        var docxBytes = await docx.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(docxBytes));
        var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        Assert.Contains("Cleared the made-up queue.", xml);
        Assert.Contains("a:blip", xml);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/sequfloh/monthly-reports/{id}/finalize")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/sequfloh/monthly-reports/{id}", new { flatRate = 80m, rowVersion = 0, body = new { primaryAchievement = "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/sequfloh/monthly-reports/{id}/reopen", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/sequfloh/monthly-reports/{id}/reopen", new { reason = "Made-up correction" })).StatusCode);
        audit = await admin.GetElAsync($"/api/audit-trail?entityType=MonthlyQualityReport&entityId={id}&pageSize=100");
        descriptions = audit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(descriptions, d => d.Contains("Finalized Monthly Quality Report - September 2026", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("Reopened Monthly Quality Report - September 2026", StringComparison.Ordinal) && d.Contains("Made-up correction", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("Exported", StringComparison.Ordinal) && d.Contains("PDF", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("Word", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Earlier_saved_months_carry_into_the_trend()
    {
        var admin = await app.AdminAsync();
        var people = await admin.GetElAsync("/api/sequfloh/monthly-reports/people");
        var person = people.EnumerateArray().First();
        var august = await (await admin.PostOkAsync("/api/sequfloh/monthly-reports", new { year = 2034, month = 8, copyText = false })).JsonAsync();
        var unknown = await admin.PutAsync($"/api/sequfloh/monthly-reports/{august.Id()}", new
        {
            flatRate = 75m,
            rowVersion = august.GetProperty("rowVersion").GetUInt32(),
            body = new { approvedById = Guid.NewGuid() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await admin.PutOkAsync($"/api/sequfloh/monthly-reports/{august.Id()}", new
        {
            flatRate = 75m,
            rowVersion = august.GetProperty("rowVersion").GetUInt32(),
            body = new
            {
                laborAmountRequested = 40m, partsAmountRequested = 10m, alertsTotal = 4, mttfDays = 11.0m, totalClaimsRaw = 6,
                primaryAchievement = "Words from the earlier month.",
                documentId = "TMP-ENG-001", documentRevision = "D", documentTitle = "Monthly Engineering Development Report",
                documentDate = "2034-08-15", approvedById = person.GetProperty("id").GetGuid(),
            },
        });
        var september = await (await admin.PostOkAsync("/api/sequfloh/monthly-reports", new { year = 2034, month = 9, copyText = true })).JsonAsync();
        var copied = september.GetProperty("body");
        Assert.Equal("D", copied.GetProperty("documentRevision").GetString());
        Assert.Equal("TMP-ENG-001", copied.GetProperty("documentId").GetString());
        Assert.Equal(person.GetProperty("id").GetGuid(), copied.GetProperty("approvedById").GetGuid());
        Assert.Equal(person.GetProperty("displayName").GetString(), copied.GetProperty("approvedByName").GetString());
        Assert.Equal("2034-09-30", copied.GetProperty("documentDate").GetString());
        var trend = september.GetProperty("trend").EnumerateArray().Single(t => t.GetProperty("month").GetInt32() == 8);
        Assert.Equal("4:1", trend.GetProperty("ratio").GetString());
        Assert.Equal(6, trend.GetProperty("totalClaims").GetInt32());
        Assert.Equal("11.0 Days", trend.GetProperty("mttf").GetString());
        var missing = september.GetProperty("trend").EnumerateArray().Single(t => t.GetProperty("month").GetInt32() == 7);
        Assert.Equal(MonthlyReportMath.EmptyNote, missing.GetProperty("ratio").GetString());
        Assert.Equal("", september.GetProperty("body").GetProperty("primaryAchievement").GetString());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("totalClaims").ValueKind);
    }

    [Fact]
    public async Task Fill_from_Sequfloh_uses_work_orders_and_does_not_replace_an_import()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        Guid partId;
        Guid recordId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var part = new Part { PartNumber = "FP-DMA-91", Name = "DMA pump", ProductFamily = "Fuel Pump", SiteId = site, Status = MasterStatus.Active };
            db.Parts.Add(part);
            var rec = new SequflohTrackingRecord
            {
                Number = "MR-" + Guid.NewGuid().ToString("N")[..8], Type = TrackingType.Warranty, SiteId = site, PartId = part.Id, PartNumber = part.PartNumber,
                PartDescription = "Made-up pump", Status = TrackingStatus.Completed, Result = "Pending", InspectionSubType = "Product alert",
            };
            db.SequflohTrackingRecords.Add(rec);
            await db.SaveChangesAsync();
            partId = part.Id;
            recordId = rec.Id;
            var when = new DateTimeOffset(2091, 1, 15, 15, 0, 0, TimeSpan.Zero);
            var userId = await db.Users.Select(u => u.Id).FirstAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sqf.sequfloh_tracking_records SET \"CreatedAt\" = {when}, \"ActualCompletion\" = {when.AddDays(2)} WHERE \"Id\" = {recordId}");
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = recordId, TrackingNumber = rec.Number, UserId = userId, UserName = "Pat", StartedAt = when, StoppedAt = when.AddHours(1), Seconds = 3600 });
            await db.SaveChangesAsync();
        }
        var created = await (await admin.PostOkAsync("/api/sequfloh/monthly-reports", new { year = 2091, month = 1, copyText = false })).JsonAsync();
        var filled = await (await admin.PostOkAsync($"/api/sequfloh/monthly-reports/{created.Id()}/fill")).JsonAsync();
        var body = filled.GetProperty("report").GetProperty("body");
        Assert.Equal(1, body.GetProperty("totalClaimsProcessed").GetInt32());
        Assert.Equal(1, body.GetProperty("dmaParts").GetInt32());
        Assert.Equal(1, body.GetProperty("alertsTotal").GetInt32());
        Assert.True(body.GetProperty("hoursInvestigating").GetDecimal() > 0);
        Assert.Contains(filled.GetProperty("notes").EnumerateArray(), n => (n.GetString() ?? "").Contains("warranty work orders", StringComparison.Ordinal));

        await Upload(admin, created.Id(), "labor-claims", "labor-claims-fake.xlsx");
        var after = await admin.GetElAsync($"/api/sequfloh/monthly-reports/{created.Id()}");
        Assert.Equal(0, after.GetProperty("body").GetProperty("totalClaimsProcessed").GetInt32());
        await admin.PostOkAsync($"/api/sequfloh/monthly-reports/{created.Id()}/fill");
        after = await admin.GetElAsync($"/api/sequfloh/monthly-reports/{created.Id()}");
        Assert.Equal(0, after.GetProperty("body").GetProperty("totalClaimsProcessed").GetInt32());
        Assert.Equal(partId, partId);
    }

    static async Task Upload(ApiClient client, Guid id, string type, string fixture)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", fixture);
        form.Add(new StringContent(type), "type");
        var response = await client.Http.PostAsync($"/api/sequfloh/monthly-reports/{id}/sources", form);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
    }
}
