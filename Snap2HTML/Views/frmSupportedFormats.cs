using Snap2HTML.Core.Models;

namespace Snap2HTML.Views;

/// <summary>
/// Dialog that displays a table of supported file formats and their
/// integrity validation capabilities (header check vs full validation).
/// Uses a DataGridView for clean tabular presentation.
/// </summary>
public sealed class frmSupportedFormats : Form
{
    private readonly DataGridView _grid;

    public frmSupportedFormats(IReadOnlyList<FormatSupportInfo> formats)
    {
        Text = "Supported Integrity Formats";
        Size = new Size(520, 480);
        MinimumSize = new Size(400, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;

        _grid = CreateGrid();
        PopulateGrid(formats);

        var legend = new Label
        {
            Text = "Header = validates file signature (magic bytes).  " +
                   "Full = deep format-specific validation.",
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 6, 0),
            ForeColor = SystemColors.GrayText
        };

        var btnClose = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
            Height = 32
        };

        Controls.Add(_grid);
        Controls.Add(legend);
        Controls.Add(btnClose);
        AcceptButton = btnClose;
    }

    private DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackgroundColor = SystemColors.Window,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = SystemColors.ControlLight,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                BackColor = SystemColors.ControlLight,
                ForeColor = SystemColors.ControlText,
                SelectionBackColor = SystemColors.ControlLight,
                SelectionForeColor = SystemColors.ControlText,
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(4, 2, 4, 2)
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                SelectionBackColor = SystemColors.Window,
                SelectionForeColor = SystemColors.ControlText,
                Padding = new Padding(4, 1, 4, 1)
            },
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
        };

        // Category column
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Category",
            Name = "Category",
            FillWeight = 30,
            MinimumWidth = 80,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold)
            }
        });

        // Extension column
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Extension",
            Name = "Extension",
            FillWeight = 30,
            MinimumWidth = 60
        });

        // Header validation column (checkbox)
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Header",
            Name = "Header",
            FillWeight = 20,
            MinimumWidth = 60
        });

        // Full validation column (checkbox)
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Full",
            Name = "Full",
            FillWeight = 20,
            MinimumWidth = 60
        });

        // Prevent checkbox editing
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 2)
            {
                grid.CancelEdit();
            }
        };

        return grid;
    }

    private void PopulateGrid(IReadOnlyList<FormatSupportInfo> formats)
    {
        _grid.Rows.Clear();
        string? lastCategory = null;

        // Alternating category colors for visual grouping
        var categoryColors = new[]
        {
            SystemColors.Window,
            Color.FromArgb(245, 245, 250)
        };
        var colorIndex = -1;

        foreach (var format in formats)
        {
            var isNewCategory = format.Category != lastCategory;
            if (isNewCategory)
            {
                colorIndex++;
                lastCategory = format.Category;
            }

            var rowIndex = _grid.Rows.Add(
                isNewCategory ? format.Category : "",
                format.Extension,
                format.SupportsHeaderValidation,
                format.SupportsFullValidation);

            var row = _grid.Rows[rowIndex];
            row.DefaultCellStyle.BackColor = categoryColors[colorIndex % categoryColors.Length];
        }
    }
}
