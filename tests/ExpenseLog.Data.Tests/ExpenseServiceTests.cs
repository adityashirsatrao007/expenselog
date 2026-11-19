using ExpenseLog.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseLog.Data.Tests;

/// <summary>
/// Runs against a real SQLite file rather than an in-memory provider: the point is to
/// prove the schema, the SQL translation and the CSV round trip, not a mock.
/// </summary>
public sealed class ExpenseServiceTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"expenselog-{Guid.NewGuid():N}.db");

    private readonly ExpenseLogContext _context;
    private readonly ExpenseService _service;

    public ExpenseServiceTests()
    {
        _context = ExpenseLogContext.Create(_databasePath);
        _context.Database.EnsureCreated();
        _service = new ExpenseService(_context);
    }

    [Fact]
    public void MoneyIsStoredAsExactCents()
    {
        var expense = _service.Add(new DateOnly(2026, 1, 5), 12.34m, "Food", "lunch");

        Assert.Equal(1234, expense.AmountCents);
        Assert.Equal(12.34m, expense.Amount);
    }

    [Fact]
    public void AmountsRoundToTheNearestCent()
    {
        var expense = _service.Add(new DateOnly(2026, 1, 5), 0.005m, "Food", "rounding");

        Assert.Equal(1, expense.AmountCents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.50)]
    public void NonPositiveAmountsAreRejected(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _service.Add(new DateOnly(2026, 1, 5), amount, "Food", "bad"));
    }

    [Fact]
    public void BlankCategoryFallsBackToTheDefault()
    {
        var expense = _service.Add(new DateOnly(2026, 1, 5), 1m, "   ", null);

        Assert.Equal(ExpenseService.DefaultCategory, expense.Category);
        Assert.Equal(string.Empty, expense.Note);
    }

    [Fact]
    public void LongTextIsTrimmedToTheColumnLimits()
    {
        var note = new string('x', 600);

        var expense = _service.Add(new DateOnly(2026, 1, 5), 1m, new string('c', 100), note);

        Assert.Equal(512, expense.Note.Length);
        Assert.Equal(64, expense.Category.Length);
    }

    [Fact]
    public void QueryFiltersByInclusiveDateRange()
    {
        _service.Add(new DateOnly(2026, 1, 31), 1m, "Food", "january");
        _service.Add(new DateOnly(2026, 2, 1), 1m, "Food", "february start");
        _service.Add(new DateOnly(2026, 2, 28), 1m, "Food", "february end");
        _service.Add(new DateOnly(2026, 3, 1), 1m, "Food", "march");

        var rows = _service.Query(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.Date.Month));
    }

    [Fact]
    public void QueryFiltersByCategory()
    {
        _service.Add(new DateOnly(2026, 1, 5), 1m, "Food", null);
        _service.Add(new DateOnly(2026, 1, 6), 1m, "Travel", null);

        var rows = _service.Query(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), category: "Travel");

        var row = Assert.Single(rows);
        Assert.Equal("Travel", row.Category);
    }

    [Fact]
    public void QuerySearchesNoteAndCategoryText()
    {
        _service.Add(new DateOnly(2026, 1, 5), 1m, "Food", "client lunch");
        _service.Add(new DateOnly(2026, 1, 6), 1m, "Travel", "train ticket");

        var rows = _service.Query(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), text: "ticket");

        var row = Assert.Single(rows);
        Assert.Equal("Travel", row.Category);
    }

    [Fact]
    public void TotalsGroupByCategoryLargestFirst()
    {
        _service.Add(new DateOnly(2026, 1, 5), 10.00m, "Food", null);
        _service.Add(new DateOnly(2026, 1, 6), 5.50m, "Food", null);
        _service.Add(new DateOnly(2026, 1, 7), 2.25m, "Travel", null);

        var totals = _service.TotalsByCategory(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Equal(2, totals.Count);
        Assert.Equal("Food", totals[0].Category);
        Assert.Equal(1550, totals[0].TotalCents);
        Assert.Equal(2, totals[0].Count);
        Assert.Equal(225, totals[1].TotalCents);
    }

    [Fact]
    public void UpdateAndDeleteMutateOnlyTheTargetRow()
    {
        var first = _service.Add(new DateOnly(2026, 1, 5), 10m, "Food", "first");
        var second = _service.Add(new DateOnly(2026, 1, 6), 20m, "Food", "second");

        _service.Update(first, new DateOnly(2026, 1, 9), 11.11m, "Bills", "edited");
        Assert.True(_service.Delete(second.Id));

        var rows = _service.Query(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var row = Assert.Single(rows);
        Assert.Equal(1111, row.AmountCents);
        Assert.Equal("Bills", row.Category);
        Assert.Equal("edited", row.Note);
    }

    [Fact]
    public void DeletingAMissingRowReturnsFalse()
    {
        Assert.False(_service.Delete(999));
    }

    [Fact]
    public void CsvExportThenImportRoundTripsCommasAndQuotes()
    {
        _service.Add(new DateOnly(2026, 1, 5), 12.34m, "Food", "lunch, with \"quotes\"");
        _service.Add(new DateOnly(2026, 1, 6), 2.25m, "Travel", "bus");

        var csv = new StringWriter();
        _service.ExportCsv(csv, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        using var target = OpenNewDatabase();
        var imported = new ExpenseService(target).ImportCsv(new StringReader(csv.ToString()));

        Assert.Equal(2, imported.Imported);
        Assert.Empty(imported.Errors);

        var rows = new ExpenseService(target).Query(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r =>
            r.Note == "lunch, with \"quotes\"" && r.AmountCents == 1234 && r.Category == "Food");
    }

    [Fact]
    public void ImportKeepsValidRowsAndReportsEachInvalidOne()
    {
        const string csv =
            "Date,Amount,Category,Note\n" +
            "2026-02-01,4.50,Food,coffee\n" +
            "01/02/2026,3.00,Food,wrong date format\n" +
            "2026-02-03,notanumber,Food,bad amount\n" +
            "2026-02-04,0,Food,zero amount\n";

        var result = _service.ImportCsv(new StringReader(csv));

        Assert.Equal(1, result.Imported);
        Assert.Equal(3, result.Errors.Count);
        Assert.Single(_service.Query(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void MalformedFileIsReportedInsteadOfThrowing()
    {
        var result = _service.ImportCsv(new StringReader("this is not a csv\t\t"));

        Assert.Equal(0, result.Imported);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void MigrationsCreateAWorkingSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"expenselog-mig-{Guid.NewGuid():N}.db");

        try
        {
            using (var context = ExpenseLogContext.Create(path))
            {
                context.Database.Migrate();
                Assert.Empty(context.Database.GetPendingMigrations());

                var service = new ExpenseService(context);
                var saved = service.Add(new DateOnly(2026, 3, 1), 9.99m, "Bills", "from migration");

                Assert.True(saved.Id > 0);
                Assert.Single(service.Query(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        TryDelete(_databasePath);
    }

    private ExpenseLogContext OpenNewDatabase()
    {
        var context = ExpenseLogContext.Create(
            Path.Combine(Path.GetTempPath(), $"expenselog-target-{Guid.NewGuid():N}.db"));

        context.Database.EnsureCreated();
        return context;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A left-over temp file is harmless for a test run.
        }
    }
}
