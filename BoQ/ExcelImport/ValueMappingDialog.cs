using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>
    /// Maps each distinct raw text value found in one Excel column to a fixed target
    /// enum option (e.g. Excel "Gövde Halkası" -> ComponentRole.MiddleElement).
    /// Pre-fills exact (case-insensitive) matches automatically; anything left as
    /// "(Atla)" is skipped by the caller (falls back to its default, never dropped silently).
    /// </summary>
    public class ValueMappingDialog : Window
    {
        private readonly string[] _rawValues;
        private readonly EnumOption[] _targets;
        private readonly Dictionary<string, ComboBox> _combos = new Dictionary<string, ComboBox>();

        public Dictionary<string, string> Result { get; private set; }

        public ValueMappingDialog(string fieldLabel, string[] rawValues, EnumOption[] targets,
                                   Dictionary<string, string> existing, Window owner)
        {
            _rawValues = rawValues;
            _targets   = targets;

            Title                 = fieldLabel + " — Değer Eşleştirme";
            Width                 = 420;
            Height                = 480;
            MinWidth              = 340;
            MinHeight             = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle           = WindowStyle.ToolWindow;
            ResizeMode            = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar         = false;
            Owner                 = owner;

            BuildUI(existing);
        }

        private void BuildUI(Dictionary<string, string> existing)
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var info = new TextBlock
            {
                Text         = "Excel'de bulunan her değeri hedef türle eşleştirin. Eşleşmeyen değerler atlanır.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(info, 0);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel();
            foreach (var raw in _rawValues)
                panel.Children.Add(BuildValueRow(raw, existing));
            scroll.Content = panel;
            Grid.SetRow(scroll, 1);

            var buttonPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var okBtn = new Button
                { Content = "Tamam", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
            okBtn.Click += (s, e) => OnOk();
            var cancelBtn = new Button
                { Content = "İptal", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 2);

            root.Children.Add(info);
            root.Children.Add(scroll);
            root.Children.Add(buttonPanel);
            Content = root;
        }

        private FrameworkElement BuildValueRow(string raw, Dictionary<string, string> existing)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

            var lbl = new TextBlock
                { Text = raw, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(lbl, 0);

            var combo = new ComboBox { DisplayMemberPath = "Display" };
            combo.Items.Add(new EnumOption("", "(Atla)"));
            foreach (var opt in _targets)
                combo.Items.Add(opt);

            string preselect = null;
            string mapped;
            if (existing != null && existing.TryGetValue(raw, out mapped))
                preselect = mapped;
            else
            {
                var auto = _targets.FirstOrDefault(t =>
                    string.Equals(t.Display, raw, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Value,   raw, StringComparison.OrdinalIgnoreCase));
                if (auto != null) preselect = auto.Value;
            }

            combo.SelectedIndex = 0;
            if (preselect != null)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    var eo = combo.Items[i] as EnumOption;
                    if (eo != null && eo.Value == preselect) { combo.SelectedIndex = i; break; }
                }
            }

            Grid.SetColumn(combo, 1);
            _combos[raw] = combo;

            row.Children.Add(lbl);
            row.Children.Add(combo);
            return row;
        }

        private void OnOk()
        {
            var result = new Dictionary<string, string>();
            foreach (var raw in _rawValues)
            {
                var eo = _combos[raw].SelectedItem as EnumOption;
                if (eo != null && !string.IsNullOrEmpty(eo.Value))
                    result[raw] = eo.Value;
            }
            Result = result;
            DialogResult = true;
        }
    }
}
