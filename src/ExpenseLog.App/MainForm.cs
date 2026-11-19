using System.Globalization;
using ExpenseLog.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseLog.App;

/// <summary>
/// One long-lived window with one long-lived DbContext. For a single-user desktop app that
/// is simpler than a unit-of-work per action, and SQLite handles the concurrency fine.
/// </summary>
public sealed class MainForm : Form
{
    private const string AllCategories = "All categories";

    private readonly ExpenseLogContext _context;
    private readonly ExpenseService _service;

    private readonly DateTimePicker _fromPicker = new() { Format = DateTimePickerFormat.Short, Width = 108 };
    private readonly DateTimePicker _toPicker = new() { Format = DateTimePickerFormat.Short, Width = 108 };
    private readonly ComboBox _categoryFilter = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _searchBox = new() { Width = 190, PlaceholderText = "Search note or category" };
    private readonly DataGridView _grid = new();
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripStatusLabel _totalLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };

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
    }

    public string DatabasePath { get; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            // Migrate, not EnsureCreated, so existing databases gain new columns over time.
            _context.Database.Migrate();
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
        ApplyFilters();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _context.Dispose();

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        Text = "ExpenseLog";
        MinimumSize = new Size(780, 480);
        Size = new Size(1060, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add(Button("Add", AddExpense));
        toolbar.Items.Add(Button("Edit", EditSelected));
        toolbar.Items.Add(Button("Delete", DeleteSelected));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button("Import CSV…", ImportCsv));
        toolbar.Items.Add(Button("Export CSV…", ExportCsv));

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

        _fromPicker.ValueChanged += (_, _) => ApplyFilters();
        _toPicker.ValueChanged += (_, _) => ApplyFilters();
        _categoryFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        _searchBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
                return;

            args.SuppressKeyPress = true;
            ApplyFilters();
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

    private void ApplyFilters()
    {
        if (!_ready)
            return;

        var (from, to) = SelectedRange();
        var category = _categoryFilter.SelectedItem as string;
        if (category == AllCategories)
            category = null;

        var expenses = _service.Query(from, to, category, _searchBox.Text);

        _grid.DataSource = expenses.Select(ExpenseRow.From).ToList();
        _countLabel.Text = $"{expenses.Count} expense(s)";
        _totalLabel.Text = $"Total: {expenses.Sum(expense => expense.Amount):N2}";
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
        ApplyFilters();
    }

    private void EditSelected()
    {
        if (SelectedRow() is not { } row)
            return;

        var expense = _context.Expenses.Find(row.Id);
        if (expense is null)
        {
            // Deleted underneath us; refresh rather than failing.
            ApplyFilters();
            return;
        }

        using var dialog = new ExpenseDialog(expense);

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _service.Update(expense, dialog.ExpenseDate, dialog.Amount, dialog.Category, dialog.Note);
        LoadCategories();
        ApplyFilters();
    }

    private void DeleteSelected()
    {
        if (SelectedRow() is not { } row)
            return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete \"{row.Note}\" ({row.Amount}) on {row.Date}?",
            "Delete expense",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirmed != DialogResult.OK)
            return;

        _service.Delete(row.Id);
        LoadCategories();
        ApplyFilters();
    }

    private void ImportCsv()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import expenses",
            Filter = "CSV file|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        ImportResult result;
        try
        {
            using var reader = new StreamReader(dialog.FileName);
            result = _service.ImportCsv(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "csv import");
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        LoadCategories();
        ApplyFilters();

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

    private void ExportCsv()
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

        try
        {
            using var writer = new StreamWriter(dialog.FileName);
            _service.ExportCsv(writer, from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "csv export");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
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
