using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UrbanoMetraj.BoQ.PipeCatalogs.UI.Views
{
    // Manages the catalog-wide pressure / stiffness class list.
    //
    // UX flow:
    //   • Click a list item  → fills TextBox (selection stays highlighted = edit mode)
    //   • Click "+ Ekle"     → always adds as new entry; case-insensitive duplicate check
    //   • Click "Güncelle"   → renames the selected item; case change is allowed;
    //                          blocks only if another entry has the same name (case-insensitive)
    //   • Click "Sil"        → removes selected item
    public class SinifYoneticisiDialog : Window
    {
        private readonly ObservableCollection<string> _classes;
        private ListBox _listBox;
        private TextBox _inputBox;

        public SinifYoneticisiDialog(ObservableCollection<string> classes, Window owner)
        {
            _classes = classes;

            Title                 = "Basınç / Rijitlik Sınıfları";
            Width                 = 300;
            Height                = 400;
            MinWidth              = 240;
            MinHeight             = 300;
            WindowStyle           = WindowStyle.ToolWindow;
            ResizeMode            = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar         = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner                 = owner;

            BuildUI();
        }

        private void BuildUI()
        {
            var root = new Grid { Margin = new Thickness(8) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Row 0: list ──────────────────────────────────────────────────
            _listBox = new ListBox { ItemsSource = _classes, Margin = new Thickness(0, 0, 0, 8) };

            // Clicking an item fills the TextBox — selection stays so Güncelle knows what to rename.
            _listBox.SelectionChanged += (s, e) =>
            {
                if (_listBox.SelectedItem is string sel)
                    _inputBox.Text = sel;
            };
            Grid.SetRow(_listBox, 0);

            // ── Row 1: label ─────────────────────────────────────────────────
            var lbl = new TextBlock
            {
                Text       = "Sınıf adı:",
                Foreground = Brushes.Gray,
                FontSize   = 11,
                Margin     = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(lbl, 1);

            // ── Row 2: input ─────────────────────────────────────────────────
            _inputBox = new TextBox
            {
                Padding = new Thickness(4, 3, 4, 3),
                Margin  = new Thickness(0, 0, 0, 6)
            };
            // Enter: if something is selected → update; otherwise → add.
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    if (_listBox.SelectedItem != null) UpdateSelected();
                    else AddNew();
                }
            };
            Grid.SetRow(_inputBox, 2);

            // ── Row 3: buttons ────────────────────────────────────────────────
            var btnPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };

            var addBtn = new Button
            {
                Content = "+ Ekle",
                Padding = new Thickness(10, 3, 10, 3),
                Margin  = new Thickness(0, 0, 4, 0)
            };
            addBtn.Click += (s, e) => AddNew();

            var updateBtn = new Button
            {
                Content = "Güncelle",
                Padding = new Thickness(10, 3, 10, 3),
                Margin  = new Thickness(0, 0, 4, 0)
            };
            updateBtn.Click += (s, e) => UpdateSelected();

            var delBtn = new Button
            {
                Content = "Sil",
                Padding = new Thickness(10, 3, 10, 3)
            };
            delBtn.Click += (s, e) =>
            {
                if (_listBox.SelectedItem is string sel)
                {
                    _classes.Remove(sel);
                    _inputBox.Clear();
                }
            };

            btnPanel.Children.Add(addBtn);
            btnPanel.Children.Add(updateBtn);
            btnPanel.Children.Add(delBtn);
            Grid.SetRow(btnPanel, 3);

            root.Children.Add(_listBox);
            root.Children.Add(lbl);
            root.Children.Add(_inputBox);
            root.Children.Add(btnPanel);
            Content = root;
        }

        // ── Add ──────────────────────────────────────────────────────────────
        // Case-insensitive duplicate check: "SN8" and "sn8" are the same entry.

        private void AddNew()
        {
            string s = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return;

            if (_classes.Any(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase)))
            {
                _inputBox.SelectAll();
                _inputBox.Focus();
                return;
            }

            _classes.Add(s);
            _listBox.ScrollIntoView(s);
            _listBox.SelectedItem = s;
            _inputBox.Clear();
            _inputBox.Focus();
        }

        // ── Update ───────────────────────────────────────────────────────────
        // Allows case change ("sn8" → "SN8").
        // Blocks only if another entry already has the same name (case-insensitive).

        private void UpdateSelected()
        {
            if (!(_listBox.SelectedItem is string old)) return;

            string s = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return;

            // No-op if text is identical (case-sensitive — so "sn8"→"SN8" proceeds)
            if (string.Equals(old, s, StringComparison.Ordinal)) return;

            // Block if another entry (not the current one) shares the same name case-insensitively
            if (_classes.Any(c => !string.Equals(c, old, StringComparison.Ordinal)
                               &&  string.Equals(c, s,   StringComparison.OrdinalIgnoreCase)))
            {
                _inputBox.SelectAll();
                _inputBox.Focus();
                return;
            }

            int idx = _classes.IndexOf(old);
            if (idx < 0) return;

            _classes[idx] = s;
            _listBox.SelectedItem = s;
            _listBox.ScrollIntoView(s);
            _inputBox.Focus();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _inputBox?.Focus();
        }
    }
}
