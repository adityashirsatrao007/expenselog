using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ExpenseLog.Data;

/// <summary>
/// All reads and writes go through here so the UI never touches <see cref="ExpenseLogContext"/>
/// directly. Money in, money out; the display layer only formats.
/// </summary>
public sealed class ExpenseService(ExpenseLogContext context)
{
    public const string DefaultCategory = "Uncategorised";

    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    public IReadOnlyList<Expense> Query(
        DateOnly from,
        DateOnly to,
        string? category = null,
        string? text = null)
    {
        var query = context.Expenses.Where(e => e.Date >= from && e.Date <= to);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{text.Trim()}%";
            query = query.Where(e =>
                EF.Functions.Like(e.Note, pattern) || EF.Functions.Like(e.Category, pattern));
        }

        return query
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Id)
            .ToList();
    }

    public IReadOnlyList<CategoryTotal> TotalsByCategory(DateOnly from, DateOnly to) =>
        context.Expenses
            .Where(e => e.Date >= from && e.Date <= to)
            // shortcut: the SQLite provider cannot translate an aggregate inside a GROUP BY
            // projection, so the grouping runs in memory after the date filter. Fine for
            // personal-scale data; replace with raw SQL if a range can exceed ~100k rows.
            .AsEnumerable()
            .GroupBy(e => e.Category)
            .Select(g => new CategoryTotal(g.Key, g.Sum(e => e.AmountCents), g.Count()))
            .OrderByDescending(t => t.TotalCents)
            .ToList();

    public IReadOnlyList<string> Categories() =>
        context.Expenses
            .Select(e => e.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

    public Expense Add(DateOnly date, decimal amount, string? category, string? note)
    {
        var expense = new Expense
        {
            Date = date,
            AmountCents = ToCents(amount),
            Category = Clean(category, DefaultCategory, 64),
            Note = Clean(note, string.Empty, 512),
        };

        context.Expenses.Add(expense);
        context.SaveChanges();
        return expense;
    }

    public void Update(Expense expense, DateOnly date, decimal amount, string? category, string? note)
    {
        expense.Date = date;
        expense.AmountCents = ToCents(amount);
        expense.Category = Clean(category, DefaultCategory, 64);
        expense.Note = Clean(note, string.Empty, 512);
        context.SaveChanges();
    }

    public bool Delete(int id)
    {
        var expense = context.Expenses.Find(id);
        if (expense is null)
            return false;

        context.Expenses.Remove(expense);
        context.SaveChanges();
        return true;
    }

    public void ExportCsv(TextWriter writer, DateOnly from, DateOnly to)
    {
        var rows = Query(from, to)
            .Select(e => new CsvRow
            {
                Date = e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount = e.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                Category = e.Category,
                Note = e.Note,
            });

        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture),
            leaveOpen: true);
        csv.WriteRecords(rows);
    }

    /// <summary>
    /// Inserts every valid row and reports the rest by line number rather than failing the
    /// whole file: a single typo in an export from elsewhere should not block the import.
    /// </summary>
    public ImportResult ImportCsv(TextReader reader)
    {
        var errors = new List<string>();
        var imported = 0;
        var lineNumber = 1;

        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
        });

        List<CsvRow> rows;
        try
        {
            if (!csv.Read())
                return new ImportResult(0, ["The file is empty."]);

            csv.ReadHeader();

            var header = csv.HeaderRecord ?? [];
            var missing = new[] { "Date", "Amount" }
                .Where(required => !header.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count > 0)
                return new ImportResult(0, [$"Missing required column(s): {string.Join(", ", missing)}."]);

            rows = csv.GetRecords<CsvRow>().ToList();
        }
        catch (CsvHelperException ex)
        {
            return new ImportResult(0, [$"Could not read the file: {ex.Message}"]);
        }

        foreach (var row in rows)
        {
            lineNumber++;

            if (!DateOnly.TryParseExact(row.Date, DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                errors.Add($"Line {lineNumber}: '{row.Date}' is not a yyyy-MM-dd date.");
                continue;
            }

            if (!decimal.TryParse(row.Amount, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out var amount) || amount <= 0)
            {
                errors.Add($"Line {lineNumber}: '{row.Amount}' is not a positive amount.");
                continue;
            }

            context.Expenses.Add(new Expense
            {
                Date = date,
                AmountCents = ToCents(amount),
                Category = Clean(row.Category, DefaultCategory, 64),
                Note = Clean(row.Note, string.Empty, 512),
            });

            imported++;
        }

        context.SaveChanges();
        return new ImportResult(imported, errors);
    }

    private static long ToCents(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");

        return checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));
    }

    private static string Clean(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class CsvRow
    {
        public string? Date { get; set; }

        public string? Amount { get; set; }

        public string? Category { get; set; }

        public string? Note { get; set; }
    }
}
