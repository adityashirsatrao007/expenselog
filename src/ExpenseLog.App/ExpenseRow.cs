using System.Globalization;
using ExpenseLog.Data;

namespace ExpenseLog.App;

/// <summary>Display row for the grid: formatted strings, so the grid never touches the entity.</summary>
public sealed class ExpenseRow
{
    public int Id { get; init; }

    public string Date { get; init; } = string.Empty;

    public string Amount { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public static ExpenseRow From(Expense expense) => new()
    {
        Id = expense.Id,
        Date = expense.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Amount = expense.Amount.ToString("N2", CultureInfo.CurrentCulture),
        Category = expense.Category,
        Note = expense.Note,
    };
}
