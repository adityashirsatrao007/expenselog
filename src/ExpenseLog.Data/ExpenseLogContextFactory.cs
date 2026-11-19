using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExpenseLog.Data;

/// <summary>Lets <c>dotnet ef migrations</c> build the context without a startup project.</summary>
public sealed class ExpenseLogContextFactory : IDesignTimeDbContextFactory<ExpenseLogContext>
{
    public ExpenseLogContext CreateDbContext(string[] args)
        => ExpenseLogContext.Create("expenselog-design.db");
}
