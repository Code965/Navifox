using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MosaicDB.App.ViewModels;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Views;

/// <summary>Costruisce le colonne dinamiche di una griglia risultati (tabelle e query).</summary>
public static class ResultGridBuilder
{
    private static readonly string[] NumericTypes = ["int", "decimal", "numeric", "float", "double", "real", "serial", "money", "bit", "year"];

    public static bool IsNumeric(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return NumericTypes.Any(type.Contains) && !type.Contains("interval") && !type.Contains("point");
    }

    public static void Rebuild(DataGrid grid, IReadOnlyList<ColumnInfo> columns, bool editable = false, Func<int, string?>? headerGlyph = null)
    {
        grid.Columns.Clear();
        for (var index = 0; index < columns.Count; index++)
        {
            var cellIndex = index;
            var column = columns[index];
            var numeric = IsNumeric(column.DataType);
            var glyph = headerGlyph?.Invoke(index);
            var gridColumn = new DataGridTemplateColumn
            {
                Header = glyph is null ? column.Name : $"{column.Name}  {glyph}",
                Tag = index,
                SortMemberPath = column.Name,
                CanUserSort = true,
                CellTemplate = new FuncDataTemplate<TableRowViewModel>((row, _) => CreateDisplay(row?.CellAt(cellIndex), numeric), supportsRecycling: false),
                IsReadOnly = !editable,
                Width = new DataGridLength(Math.Clamp((column.Name.Length + (glyph?.Length + 2 ?? 0)) * 8.5 + 44, 70, 240))
            };
            if (editable)
                gridColumn.CellEditingTemplate = new FuncDataTemplate<TableRowViewModel>((row, _) => CreateEditor(row?.CellAt(cellIndex), numeric), supportsRecycling: false);
            grid.Columns.Add(gridColumn);
        }
    }

    private static Control CreateDisplay(CellViewModel? cell, bool numeric)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(6, 0)
        };
        if (cell is null) return text;
        text.Bind(TextBlock.TextProperty, new Binding(nameof(CellViewModel.Text)) { Source = cell });
        var border = new Border { Child = text, Classes = { "cell" } };
        void Sync()
        {
            text.Classes.Set("null-cell", cell.IsNull);
            text.FontStyle = cell.IsNull ? FontStyle.Italic : FontStyle.Normal;
            border.Classes.Set("modified", cell.IsModified);
        }
        Sync();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Sync();
        border.AttachedToVisualTree += (_, _) => { cell.PropertyChanged -= OnChanged; cell.PropertyChanged += OnChanged; Sync(); };
        border.DetachedFromVisualTree += (_, _) => cell.PropertyChanged -= OnChanged;
        return border;
    }

    private static Control CreateEditor(CellViewModel? cell, bool numeric)
    {
        var box = new TextBox
        {
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(5, 0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Watermark = cell?.IsNull == true ? "(NULL)" : null
        };
        if (cell is not null) box.Bind(TextBox.TextProperty, new Binding(nameof(CellViewModel.EditText)) { Source = cell, Mode = BindingMode.TwoWay });
        return box;
    }
}
