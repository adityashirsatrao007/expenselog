using Microsoft.EntityFrameworkCore;

namespace ExpenseLog.Data;

public sealed class ExpenseLogContext : DbContext
{
    public ExpenseLogContext(DbContextOptions<ExpenseLogContext> options)
        : base(options)
    {
    }

    public DbSet<Expense> Expenses => Set<Expense>();

    /// <summary>Builds a context for a SQLite file; the app and the tests both use this.</summary>
    public static ExpenseLogContext Create(string databasePath)
    {
        var options = new DbContextOptionsBuilder<ExpenseLogContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new ExpenseLogContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var expense = modelBuilder.Entity<Expense>();

        expense.ToTable("Expenses");
        expense.HasKey(e => e.Id);
        expense.Property(e => e.Category).IsRequired().HasMaxLength(64);
        expense.Property(e => e.Note).HasMaxLength(512);

        // The main list is always filtered and ordered by date.
        expense.HasIndex(e => e.Date);
        expense.HasIndex(e => e.Category);
    }
}
