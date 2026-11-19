using System.IO;
using ExpenseLog.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseLog.App;

/// <summary>
/// One window with one long-lived DbContext. Queries and CSV work run on the thread pool
/// behind a gate, because <see cref="ExpenseLogContext"/> is not thread-safe and a slow
/// query used to freeze the window.
/// </summary>
public sealed class MainForm : Form
{
    private const string AllCategories = "All categories";

    private readonly ExpenseLogContext _context;
    private readonly ExpenseService _service;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    private readonly DateTimePicker _fromPicker = new() { Format = DateTimePickerFormat.Short, Width = 108 };
    private readonly DateTimePicker _toPicker = new() { Format = DateTimePickerFormat.Short, Width = 108 };
    private readonly ComboBox _categoryFilter = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _searchBox = new() { Width = 190, PlaceholderText = "Search note or category" };
    private readonly DataGridView _grid = new();
    private readonly ToolStripButton _undoButton = new("Undo delete")
    {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
    };

    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripStatusLabel _totalLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };

    private (DateOnly Date, decimal Amount, string Category, string Note)? _lastDeleted;
    private int _filterGeneration;
    private bool _ready;

    public MainForm()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExpenseLog");

        Directory.CreateDirectory(folder);
        DatabasePath = Path.Combine(folder, "expenses.db");

        _context = ExpenseLogContext.Create(DatabasePath);
        _service = new ExpenseService(_context);

        BuildUi();
        _undoButton.Enabled = false;
    }

    public string DatabasePath { get; }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            // Migrate, not EnsureCreated, so existing databases gain new columns over time.
            await Task.Run(() => _context.Database.Migrate());
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "database migrate");
            MessageBox.Show(
                this,
                $"The database could not be opened:\n{DatabasePath}\n\n{ex.Message}",
                "ExpenseLog",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        var today = DateTime.Today;
        _fromPicker.Value = new DateTime(today.Year, today.Month, 1);
        _toPicker.Value = today;

        LoadCategories();
        _ready = true;
        await ApplyFiltersAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _context.Dispose();
            _databaseGate.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        Text = "ExpenseLog";
        MinimumSize = new Size(820, 480);
        Size = new Size(1060, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add(Button("Add", AddExpense));
        toolbar.Items.Add(Button("Edit", EditSelected));
        toolbar.Items.Add(Button("Delete", DeleteSelected));
        toolbar.Items.Add(_undoButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button("Import CSV…", () => _ = ImportCsvAsync()));
        toolbar.Items.Add(Button("Export CSV…", () => _ = ExportCsvAsync()));

        _undoButton.Click += (_, _) => UndoDelete();

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 0),
            WrapContents = false,
        };

        filters.Controls.Add(Labeled("From", _fromPicker));
        filters.Controls.Add(Labeled("To", _toPicker));
        filters.Controls.Add(Labeled("Category", _categoryFilter));
        filters.Controls.Add(Labeled("Search", _searchBox));

        ConfigureGrid();

        var status = new StatusStrip();
        status.Items.Add(_countLabel);
        status.Items.Add(_totalLabel);

        _fromPicker.ValueChanged += async (_, _) => await ApplyFiltersAsync();
        _toPicker.ValueChanged += async (_, _) => await ApplyFiltersAsync();
        _categoryFilter.SelectedIndexChanged += async (_, _) => await ApplyFiltersAsync();
        _searchBox.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
                return;

            args.SuppressKeyPress = true;
            await ApplyFiltersAsync();
        };

        // Fill is added first so the docked bars claim their space before the grid fills the rest.
        Controls.Add(_grid);
        Controls.Add(filters);
        Controls.Add(toolbar);
        Controls.Add(status);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0)
                EditSelected();
        };

        _grid.Columns.Add(Column("Date", nameof(ExpenseRow.Date), 15));
        _grid.Columns.Add(Column("Amount", nameof(ExpenseRow.Amount), 15, rightAlign: true));
        _grid.Columns.Add(Column("Category", nameof(ExpenseRow.Category), 20));
        _grid.Columns.Add(Column("Note", nameof(ExpenseRow.Note), 50));
    }

    /// <summary>
    /// Runs the query off the UI thread and discards the result if a newer filter was applied
    /// while it was running — the classic stale-response race on a filtered grid.
    /// </summary>
    private async Task ApplyFiltersAsync()
    {
        if (!_ready)
            return;

        var generation = ++_filterGeneration;
        var (from, to) = SelectedRange();
        var category = _categoryFilter.SelectedItem as string;
        if (category == AllCategories)
            category = null;

        var text = _searchBox.Text;

        List<Expense> expenses = [];
        List<CategoryTotal> totals = [];

        try
        {
            await _databaseGate.WaitAsync();
            try
            {
                (expenses, totals) = await Task.Run(() =>
                {
                    var rows = _service.Query(from, to, category, text).ToList();
                    var grouped = _service.TotalsByCategory(from, to).ToList();
                    return (rows, grouped);
                });
            }
            finally
            {
                _databaseGate.Release();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "query");
            _countLabel.Text = "Query failed";
            MessageBox.Show(this, ex.Message, "Could not load expenses", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (generation != _filterGeneration)
            return;

        _grid.DataSource = expenses.Select(ExpenseRow.From).ToList();
        _countLabel.Text = $"{expenses.Count} expense(s)";

        var total = expenses.Sum(expense => expense.Amount);
        var summary = totals.Count == 0
            ? $"Total: {total:N2}"
            : $"Total: {total:N2}   ·   {totals[0].Category}: {totals[0].Total:N2} of {totals.Count} categories";

        _totalLabel.Text = summary;
    }

    private void LoadCategories()
    {
        var selected = _categoryFilter.SelectedItem as string ?? AllCategories;

        _categoryFilter.Items.Clear();
        _categoryFilter.Items.Add(AllCategories);

        foreach (var category in _service.Categories())
            _categoryFilter.Items.Add(category);

        _categoryFilter.SelectedItem = _categoryFilter.Items.Contains(selected)
            ? selected
            : AllCategories;
    }

    private void AddExpense()
    {
        using var dialog = new ExpenseDialog();

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _service.Add(dialog.ExpenseDate, dialog.Amount, dialog.Category, dialog.Note);
        LoadCategories();
        _ = ApplyFiltersAsync();
    }

    private void EditSelected()
    {
        if (SelectedRow() is not { } row)
            return;

        var expense = _context.Expenses.Find(row.Id);
        if (expense is null)
        {
            // Deleted underneath us; refresh rather than failing.
            _ = ApplyFiltersAsync();
            return;
        }

        using var dialog = new ExpenseDialog(expense);

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _service.Update(expense, dialog.ExpenseDate, dialog.Amount, dialog.Category, dialog.Note);
        LoadCategories();
        _ = ApplyFiltersAsync();
    }

    private void DeleteSelected()
    {
        if (SelectedRow() is not { } row)
            return;

        var expense = _context.Expenses.Find(row.Id);
        if (expense is null)
        {
            _ = ApplyFiltersAsync();
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Delete \"{row.Note}\" ({row.Amount}) on {row.Date}?\n\nYou can undo this from the toolbar.",
            "Delete expense",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirmed != DialogResult.OK)
            return;

        // Snapshot before deleting so undo can re-insert the row.
        _lastDeleted = (expense.Date, expense.Amount, expense.Category, expense.Note);
        _undoButton.Enabled = true;

        _service.Delete(row.Id);
        LoadCategories();
        _ = ApplyFiltersAsync();
    }

    private void UndoDelete()
    {
        if (_lastDeleted is not { } snapshot)
            return;

        // Re-inserted with a new id; the row id is an implementation detail, not data.
        _service.Add(snapshot.Date, snapshot.Amount, snapshot.Category, snapshot.Note);

        _lastDeleted = null;
        _undoButton.Enabled = false;

        LoadCategories();
        _ = ApplyFiltersAsync();
    }

    private async Task ImportCsvAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import expenses",
            Filter = "CSV file|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        ImportResult result;

        UseWaitCursor = true;
        try
        {
            await _databaseGate.WaitAsync();
            try
            {
                using var reader = new StreamReader(dialog.FileName);
                result = await Task.Run(() => _service.ImportCsv(reader));
            }
            finally
            {
                _databaseGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "csv import");
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        LoadCategories();
        _ = ApplyFiltersAsync();

        var message = $"{result.Imported} row(s) imported from {Path.GetFileName(dialog.FileName)}.";

        if (result.Errors.Count > 0)
        {
            var shown = string.Join(Environment.NewLine, result.Errors.Take(10));
            message += $"{Environment.NewLine}{Environment.NewLine}{result.Errors.Count} row(s) skipped:{Environment.NewLine}{shown}";
        }

        MessageBox.Show(
            this,
            message,
            "Import finished",
            MessageBoxButtons.OK,
            result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ExportCsvAsync()
    {
        var (from, to) = SelectedRange();

        using var dialog = new SaveFileDialog
        {
            Title = "Export expenses",
            Filter = "CSV file|*.csv",
            FileName = $"expenses-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        UseWaitCursor = true;
        try
        {
            await _databaseGate.WaitAsync();
            try
            {
                using var writer = new StreamWriter(dialog.FileName);
                await Task.Run(() => _service.ExportCsv(writer, from, to));
            }
            finally
            {
                _databaseGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "csv export");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        MessageBox.Show(
            this,
            $"Written to {dialog.FileName}",
            "Export finished",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private (DateOnly From, DateOnly To) SelectedRange() =>
        (DateOnly.FromDateTime(_fromPicker.Value.Date), DateOnly.FromDateTime(_toPicker.Value.Date));

    private ExpenseRow? SelectedRow() => _grid.CurrentRow?.DataBoundItem as ExpenseRow;

    private static ToolStripButton Button(string text, Action onClick) => new(text, null, (_, _) => onClick())
    {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
    };

    private static FlowLayoutPanel Labeled(string label, Control control)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 14, 0) };

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
        });

        panel.Controls.Add(control);
        return panel;
    }

    private static DataGridViewTextBoxColumn Column(
        string header,
        string property,
        int fillWeight,
        bool rightAlign = false) => new()
    {
        HeaderText = header,
        DataPropertyName = property,
        FillWeight = fillWeight,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        DefaultCellStyle = new DataGridViewCellStyle
        {
            Alignment = rightAlign ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft,
        },
    };
}
