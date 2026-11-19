using System.Globalization;
using ExpenseLog.Data;

namespace ExpenseLog.App;

/// <summary>Add/edit dialog. Validation happens here so the service can keep throwing on bad input.</summary>
public sealed class ExpenseDialog : Form
{
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Width = 170 };
    private readonly TextBox _amount = new() { Width = 170, TextAlign = HorizontalAlignment.Right };
    private readonly ComboBox _category = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _note = new() { Width = 340, Multiline = true, Height = 64, ScrollBars = ScrollBars.Vertical };
    private readonly Label _error = new() { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(340, 0) };

    public ExpenseDialog()
        : this(null)
    {
    }

    public ExpenseDialog(Expense? expense)
    {
        Text = expense is null ? "Add expense" : "Edit expense";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(400, 320);

        foreach (var suggestion in new[] { "Food", "Travel", "Bills", "Supplies", "Other" })
            _category.Items.Add(suggestion);

        if (expense is null)
        {
            _date.Value = DateTime.Today;
            _category.Text = "Other";
        }
        else
        {
            _date.Value = expense.Date.ToDateTime(TimeOnly.MinValue);
            _amount.Text = expense.Amount.ToString("0.00", CultureInfo.CurrentCulture);
            _category.Text = expense.Category;
            _note.Text = expense.Note;
        }

        BuildUi();

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
    }

    public DateOnly ExpenseDate { get; private set; }

    public decimal Amount { get; private set; }

    public string Category { get; private set; } = ExpenseService.DefaultCategory;

    public string Note { get; private set; } = string.Empty;

    private readonly Button _saveButton = new() { Text = "Save", DialogResult = DialogResult.None, Width = 88 };
    private readonly Button _cancelButton = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88 };

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = true,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Date", _date);
        AddRow(layout, 1, "Amount", _amount);
        AddRow(layout, 2, "Category", _category, "Pick a suggestion or type a new one.");
        AddRow(layout, 3, "Note", _note);

        layout.Controls.Add(_error, 1, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
        };

        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_cancelButton);
        layout.Controls.Add(buttons, 1, 5);

        _saveButton.Click += OnSave;

        Controls.Add(layout);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control, string? hint = null)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 8, 10, 0),
        }, 0, row);

        if (hint is null)
        {
            control.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(control, 1, row);
            return;
        }

        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.Add(control);
        stack.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText });
        layout.Controls.Add(stack, 1, row);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)
            || amount <= 0)
        {
            _error.Text = "Enter an amount greater than zero.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_category.Text))
        {
            _error.Text = "Choose or type a category.";
            return;
        }

        _error.Text = string.Empty;
        ExpenseDate = DateOnly.FromDateTime(_date.Value.Date);
        Amount = amount;
        Category = _category.Text.Trim();
        Note = _note.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
