using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>
    /// Generic "which Excel column feeds which catalog field" mapping dialog.
    /// Reusable across any catalog (Boru Kataloğu, Baca Parça Kataloğu, future ones) —
    /// callers only supply the target field list; this dialog never knows about the
    /// concrete model types being built.
    /// </summary>
    public class ColumnMappingDialog : Window
    {
        private readonly string[] _headers;
        private readonly List<string[]> _rows;
        private readonly MappableField[] _fields;

        private readonly Dictionary<string, ComboBox> _columnCombos = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, Dictionary<string, string>> _valueMaps
            = new Dictionary<string, Dictionary<string, string>>();

        public ColumnMappingResult Result { get; private set; }

        public ColumnMappingDialog(string fileName, string[] headers, List<string[]> rows,
                                    MappableField[] fields, Window owner)
        {
            _headers = headers;
            _rows    = rows;
            _fields  = fields;

            Title                 = "Excel Sütun Eşleştirme";
            Width                 = 640;
            Height                = 580;
            MinWidth              = 520;
            MinHeight             = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle           = WindowStyle.ToolWindow;
            ResizeMode            = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar         = false;
            Owner                 = owner;

            BuildUI(fileName);
        }

        private void BuildUI(string fileName)
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var info = new TextBlock
            {
                Text = "Dosya: " + fileName + "   —   " + _rows.Count + " veri satırı bulundu.\n" +
                       "Her alan için hangi Excel sütununun kullanılacağını seçin. Boş bırakılan alanlar içe aktarılmaz.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(info, 0);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var fieldsPanel = new StackPanel();
            foreach (var field in _fields)
                fieldsPanel.Children.Add(BuildFieldRow(field));
            scroll.Content = fieldsPanel;
            Grid.SetRow(scroll, 1);

            var previewContainer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var previewLabel = new TextBlock
            {
                Text       = "Önizleme (ilk satırlar):",
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(previewLabel, Dock.Top);
            previewContainer.Children.Add(previewLabel);
            previewContainer.Children.Add(BuildPreviewGrid());
            Grid.SetRow(previewContainer, 2);

            var buttonPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var okBtn = new Button
                { Content = "İçe Aktar", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
            okBtn.Click += (s, e) => OnOk();
            var cancelBtn = new Button
                { Content = "İptal", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 3);

            root.Children.Add(info);
            root.Children.Add(scroll);
            root.Children.Add(previewContainer);
            root.Children.Add(buttonPanel);
            Content = root;
        }

        private FrameworkElement BuildFieldRow(MappableField field)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);

            var combo = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 6, 0) };
            combo.Items.Add(new ColumnOption(-1, "(Kullanma)"));
            for (int i = 0; i < _headers.Length; i++)
                combo.Items.Add(new ColumnOption(i, (i + 1) + ". " + _headers[i]));
            combo.SelectedIndex = GuessDefaultColumn(field);
            _columnCombos[field.Key] = combo;
            Grid.SetColumn(combo, 1);

            grid.Children.Add(label);
            grid.Children.Add(combo);

            if (field.IsEnum)
            {
                var mapBtn = new Button { Content = "Değerleri Eşleştir…", Padding = new Thickness(8, 2, 8, 2) };
                mapBtn.Click += (s, e) => OnMapValues(field, combo);
                Grid.SetColumn(mapBtn, 2);
                grid.Children.Add(mapBtn);
            }

            return grid;
        }

        // Best-effort auto-pick: matches an Excel header that equals the field's label or key.
        private int GuessDefaultColumn(MappableField field)
        {
            for (int i = 0; i < _headers.Length; i++)
            {
                if (string.Equals(_headers[i], field.Label, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_headers[i], field.Key,   StringComparison.OrdinalIgnoreCase))
                    return i + 1; // +1 : index 0 is "(Kullanma)"
            }
            return 0;
        }

        private void OnMapValues(MappableField field, ComboBox combo)
        {
            var opt = combo.SelectedItem as ColumnOption;
            if (opt == null || opt.Index < 0)
            {
                MessageBox.Show("Önce bu alan için bir Excel sütunu seçin.", "Sütun Seçilmedi",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var distinctValues = _rows
                .Select(r => opt.Index < r.Length ? (r[opt.Index] ?? "").Trim() : "")
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToArray();

            if (distinctValues.Length == 0)
            {
                MessageBox.Show("Seçili sütunda değer bulunamadı.", "Boş Sütun",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Dictionary<string, string> existing;
            _valueMaps.TryGetValue(field.Key, out existing);

            var dlg = new ValueMappingDialog(field.Label, distinctValues, field.EnumOptions, existing, this);
            if (dlg.ShowDialog() == true)
                _valueMaps[field.Key] = dlg.Result;
        }

        private DataGrid BuildPreviewGrid()
        {
            var dt = new DataTable();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in _headers)
            {
                string name = h;
                int suffix = 2;
                while (!usedNames.Add(name))
                    name = h + " (" + suffix++ + ")";
                dt.Columns.Add(name);
            }
            foreach (var row in _rows.Take(8))
                dt.Rows.Add(row.Cast<object>().ToArray());

            var grid = new DataGrid
            {
                ItemsSource         = dt.DefaultView,
                IsReadOnly          = true,
                AutoGenerateColumns = true,
                CanUserAddRows      = false,
                HeadersVisibility   = DataGridHeadersVisibility.Column
            };
            return grid;
        }

        private void OnOk()
        {
            var result = new ColumnMappingResult();
            bool anyMapped = false;
            foreach (var field in _fields)
            {
                var opt = _columnCombos[field.Key].SelectedItem as ColumnOption;
                int idx = opt != null ? opt.Index : -1;
                result.ColumnIndex[field.Key] = idx;
                if (idx >= 0) anyMapped = true;

                Dictionary<string, string> vmap;
                if (field.IsEnum && _valueMaps.TryGetValue(field.Key, out vmap))
                    result.ValueMaps[field.Key] = vmap;
            }

            if (!anyMapped)
            {
                MessageBox.Show("En az bir alan için sütun seçmelisiniz.", "Eşleştirme Eksik",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = result;
            DialogResult = true;
        }

        private sealed class ColumnOption
        {
            public int    Index { get; }
            public string Label { get; }
            public ColumnOption(int index, string label) { Index = index; Label = label; }
        }
    }
}
