# ExpenseLog

A local expense tracker for Windows. Records expenses, filters them by date, category or
free text, totals them per category, and imports or exports CSV.

[![build](https://github.com/adityashirsatrao007/expenselog/actions/workflows/build.yml/badge.svg)](https://github.com/adityashirsatrao007/expenselog/actions/workflows/build.yml)

WinForms on .NET 8 with EF Core and SQLite. The database layer is a separate project with no
UI dependency, so all sixteen test cases run on any OS against a real SQLite file.

## What it does

- Add, edit and delete expenses (date, amount, category, note)
- Filter by date range, category and text search across note and category
- Running total and row count for the current filter
- Category totals, largest first
- **Import CSV** — validates each row, imports the good ones and reports the rest by line number
- **Export CSV** for the selected date range
- Data lives in `%AppData%\ExpenseLog\expenses.db`; nothing leaves the machine

## Design decisions

### Money is integer cents

`AmountCents` is a `long`; `Amount` is a computed `decimal`. SQLite has no decimal type, and
storing money as a floating-point number makes totals drift (`0.1 + 0.2 != 0.3`). Formatting
happens only in the grid, so arithmetic is always exact.

### Migrations, not `EnsureCreated`

The app calls `Database.Migrate()` at startup, so a future schema change reaches existing
databases instead of being silently ignored. The initial migration is checked in under
`src/ExpenseLog.Data/Migrations`; regenerate with:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> --project src/ExpenseLog.Data --output-dir Migrations
```

### One known EF Core limitation, handled explicitly

The SQLite provider cannot translate an aggregate inside a `GROUP BY` projection, so
`TotalsByCategory` filters in SQL and groups in memory:

```csharp
context.Expenses
    .Where(e => e.Date >= from && e.Date <= to)
    .AsEnumerable()   // shortcut: SQLite cannot SUM inside GROUP BY
    .GroupBy(e => e.Category)
    ...
```

That is correct for personal-scale data (thousands of rows) and honest about the limit; a raw
`GROUP BY` query is the upgrade path if a range can exceed roughly 100k rows. The alternative —
`AsNoTracking` plus a client-side group over the whole table — would have been worse, because
it loses the indexed date filter.

### Import degrades instead of failing

`ImportCsv` returns `ImportResult(int Imported, IReadOnlyList<string> Errors)`. A file with
three bad rows out of a thousand imports the 997 and tells you which lines to fix. A file whose
header lacks `Date` or `Amount` is rejected before any row is touched. Silent partial imports
are how people lose data, so the result is always shown in a dialog.

### WinForms, and procedural on purpose

WinForms is still what most enterprise Windows line-of-business work is written in, and a
`DataGridView` bound to a formatted row list is a few dozen lines. The window builds its
controls in code rather than a `.Designer.cs` file: the layout is readable in one place and
diffs are reviewable. Business rules live in `ExpenseLog.Data`, where they are tested.

## Layout

```
src/
  ExpenseLog.Data/            net8.0, no UI types
    Expense.cs                entity, CategoryTotal, ImportResult
    ExpenseLogContext.cs      DbContext, indexes, Create()
    ExpenseService.cs         query, totals, add/update/delete, CSV import/export
    Migrations/               checked-in InitialCreate
  ExpenseLog.App/             net8.0-windows
    Program.cs                entry point, crash logging
    MainForm.cs               toolbar, filters, grid, totals
    ExpenseDialog.cs          add/edit with validation
tests/
  ExpenseLog.Data.Tests/      16 xUnit tests against real SQLite files
```

## Download and run

Every push to `main` publishes a single self-contained `ExpenseLog.exe` for `win-x64`, with the
.NET runtime and the SQLite native library bundled — nothing needs to be installed first.
Download it from the artifacts of the latest green run: **Actions → the newest `build` run →
Artifacts → `ExpenseLog-win-x64`**.

The executable is unsigned, so SmartScreen warns on first launch: *More info → Run anyway*.

## Build and run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet run --project src/ExpenseLog.App
```

```bash
dotnet test
```

The database is created on first run at `%AppData%\ExpenseLog\expenses.db`.

## Tests

| Test | What it guards |
| --- | --- |
| `MoneyIsStoredAsExactCents` | 12.34 is stored as 1234 and survives the round trip |
| `AmountsRoundToTheNearestCent` | Half-cent input does not silently truncate |
| `NonPositiveAmountsAreRejected` | Zero and negative amounts never reach the database |
| `BlankCategoryFallsBackToTheDefault` | Missing category cannot break the insert |
| `LongTextIsTrimmedToTheColumnLimits` | 600 characters cannot violate the 512 limit |
| `QueryFiltersByInclusiveDateRange` | Month boundaries are inclusive on both ends |
| `QueryFiltersByCategory` | Category filter is exact |
| `QuerySearchesNoteAndCategoryText` | Search covers both columns |
| `TotalsGroupByCategoryLargestFirst` | Grouping, summing and ordering |
| `UpdateAndDeleteMutateOnlyTheTargetRow` | No collateral writes |
| `DeletingAMissingRowReturnsFalse` | Deleting a gone row is not a crash |
| `CsvExportThenImportRoundTripsCommasAndQuotes` | Quoting survives export → import |
| `ImportKeepsValidRowsAndReportsEachInvalidOne` | Partial import is intended, and reported |
| `MalformedFileIsReportedInsteadOfThrowing` | A wrong file produces a message, not an exception |
| `MigrationsCreateAWorkingSchema` | The checked-in migration actually applies |

## Operational behaviour

- **Queries, imports and exports run off the UI thread**, serialised behind a gate because
  `DbContext` is not thread-safe. A slow query no longer freezes the window.
- **Stale responses are discarded.** Each filter change increments a generation counter; a late
  result from an older filter is dropped instead of overwriting the grid.
- **One instance only.** A named mutex stops a second copy, which would otherwise fight the first
  over the same SQLite file and surface "database is locked".
- **Delete is undoable** from the toolbar, and undo re-inserts the row with a new id.
- **Failures are logged** to `%AppData%\ExpenseLog\crash.log` and shown once, so the window never
  closes silently.

## What I'd do next

- Charts for monthly spend by category (`System.Windows.Forms.DataVisualization`)
- Budgets per category with over-budget highlighting
- Multi-currency with a stored exchange rate per row
- MSIX packaging and code signing so installs don't trip SmartScreen
- Bulk edit and inline validation via `DataGridView` error providers
- Undo history longer than one step

## License

MIT — see [LICENSE](LICENSE).
