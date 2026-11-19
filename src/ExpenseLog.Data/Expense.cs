using System.ComponentModel.DataAnnotations.Schema;

namespace ExpenseLog.Data;

/// <summary>
/// Money is stored as integer cents. SQLite has no decimal type, and a floating-point
/// amount would make totals drift; cents keep every sum exact.
/// </summary>
public sealed class Expense
{
    public int Id { get; set; }

    public DateOnly Date { get; set; }

    public long AmountCents { get; set; }

    public string Category { get; set; } = "Uncategorised";

    public string Note { get; set; } = string.Empty;

    [NotMapped]
    public decimal Amount => AmountCents / 100m;
}

public sealed record CategoryTotal(string Category, long TotalCents, int Count)
{
    public decimal Total => TotalCents / 100m;
}

public sealed record ImportResult(int Imported, IReadOnlyList<string> Errors);
