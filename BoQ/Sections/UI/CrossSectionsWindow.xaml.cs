using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using ColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;
using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.Sections.UI
{
    /// <summary>What the user asked for when the panel closed.</summary>
    public enum PanelResult
    {
        Close = 0,
        Draw = 1,
        /// <summary>Pick pipes in the drawing as the scope, then reopen the panel.</summary>
        CadSelect = 2,
        /// <summary>Pick plan points on pipes → one section each.</summary>
        PickSingle = 3
    }

    /// <summary>
    /// UT_CROSS_SECTIONS control panel: Şablon (template) management + scope / scenario /
    /// content / appearance / layout tabs + live preview of a real section. Every edit
    /// re-renders the preview (debounced) with the same layout engine the drawing uses.
    /// </summary>
    public partial class CrossSectionsWindow : Window
    {
        private readonly CrossSectionsVm _vm;
        private readonly DispatcherTimer _timer;
        public PanelResult Result { get; private set; } = PanelResult.Close;
        /// <summary>Set by the "Kesitleri Sil" menu: null = nothing, "" = all, else a run id.</summary>
        public Func<string, int> ClearHandler { get; set; }
        public string LastRunId { get; set; }
        /// <summary>Network → ACI from the networks' drawing layers (supplied by the command).</summary>
        public Func<System.Collections.Generic.IDictionary<string, short>> NetworkLayerColors { get; set; }

        public CrossSectionsWindow(CrossSectionsVm vm, string[] textStyles)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            TextStyleCombo.ItemsSource = textStyles;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => { _timer.Stop(); Refresh(); };
            vm.RequestPreview = Schedule;

            // Any edit anywhere → debounced preview refresh (bindings write straight into the style).
            AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler((s, e) => Schedule()));
            AddHandler(ToggleButton.CheckedEvent,    new RoutedEventHandler((s, e) => Schedule()));
            AddHandler(ToggleButton.UncheckedEvent,  new RoutedEventHandler((s, e) => Schedule()));
            AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler((s, e) => Schedule()));
            Loaded += (s, e) => Refresh();
            Preview.SizeChanged += (s, e) => Schedule();
        }

        private void Schedule() { _timer.Stop(); _timer.Start(); }

        private void Refresh()
        {
            try
            {
                var sheet = _vm.BuildPreview(out string note);
                Preview.Show(sheet, _vm.Style, note);
            }
            catch (Exception ex) { Preview.Show((SectionSheet)null, _vm.Style, "Önizleme hatası: " + ex.Message); }
            _vm.RefreshDirty();
            _vm.RaiseWarnings();
        }

        // ── Şablon ──────────────────────────────────────────────────────────
        private void SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            try { _vm.SaveTemplate(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Şablon kaydedilemedi", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void SaveTemplateAs_Click(object sender, RoutedEventArgs e)
        {
            var name = TextPrompt.Ask(this, "Farklı Kaydet", "Şablon adı:", _vm.Style.Name + " (kopya)");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (_vm.Templates.Contains(name.Trim()) &&
                MessageBox.Show(this, $"'{name}' adlı şablon var. Üzerine yazılsın mı?", "Farklı Kaydet",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _vm.SaveTemplate(name); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Şablon kaydedilemedi", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, $"'{_vm.Style.Name}' şablon dosyası silinsin mi? (Açık ayarlar korunur.)", "Şablonu Sil",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _vm.DeleteTemplate(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Silinemedi", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Kesit şablonu (*.xml)|*.xml", Title = "Şablon içe aktar" };
            if (dlg.ShowDialog(this) != true) return;
            try { _vm.LoadStyle(SectionStyleStore.Import(dlg.FileName)); _vm.SaveTemplate(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "İçe aktarılamadı", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Kesit şablonu (*.xml)|*.xml", Title = "Şablon dışa aktar", FileName = _vm.Style.Name + ".xml"
            };
            if (dlg.ShowDialog(this) != true) return;
            try { SectionStyleStore.Export(_vm.Style, dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dışa aktarılamadı", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // ── Kapsam ──────────────────────────────────────────────────────────
        private void SelectAll_Click(object sender, RoutedEventArgs e)   => _vm.SelectPipes(p => true, true);
        private void SelectNone_Click(object sender, RoutedEventArgs e)  => _vm.SelectPipes(p => true, false);
        private void SelectClash_Click(object sender, RoutedEventArgs e)
        {
            _vm.SelectPipes(p => true, false);
            _vm.SelectPipes(p => p.Clash, true);
        }
        private void SelectNetwork_Click(object sender, RoutedEventArgs e)
        {
            if (!(NetworkPick.SelectedItem is string net)) return;
            _vm.SelectPipes(p => true, false);
            _vm.SelectPipes(p => string.Equals(p.Network, net, StringComparison.OrdinalIgnoreCase), true);
        }
        private void CadSelect_Click(object sender, RoutedEventArgs e) { CommitEdits(); Result = PanelResult.CadSelect; Close(); }

        private void NetworkLayerColors_Click(object sender, RoutedEventArgs e)
        {
            var colors = NetworkLayerColors?.Invoke();
            if (colors == null || colors.Count == 0)
            {
                MessageBox.Show(this, "Ağ adlarıyla başlayan katman bulunamadı.", "Ağ renkleri", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _vm.ApplyNetworkColors(colors);
        }

        private void SubLayerColor_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as SubLayerRowVm;
            var sel = SubLayerGrid.SelectedItems.OfType<SubLayerRowVm>().ToArray();
            var t = row != null && sel.Contains(row) ? sel : row != null ? new[] { row } : sel;
            if (t.Length == 0) return;
            var c = PickAci(t[0].HatchColorAci, true);
            if (!c.HasValue) return;
            foreach (var r in t) r.HatchColorAci = c.Value;
            Schedule();
        }

        // ── İçerik ──────────────────────────────────────────────────────────
        private void RowUp_Click(object sender, RoutedEventArgs e)   { if ((sender as FrameworkElement)?.DataContext is TableRowVm r) _vm.MoveTableRow(r, -1); }
        private void RowDown_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is TableRowVm r) _vm.MoveTableRow(r, +1); }

        private void NetworkColor_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is NetworkColorVm n)) return;
            var c = PickAci(n.Aci, false);
            if (c.HasValue) { n.Aci = c.Value; Schedule(); }
        }

        // ── Görünüm (colour pickers apply to every selected row) ─────────────
        private ElementRowVm[] Targets(object sender)
        {
            var row = (sender as FrameworkElement)?.DataContext as ElementRowVm;
            var sel = ElementGrid.SelectedItems.OfType<ElementRowVm>().ToArray();
            return row != null && sel.Contains(row) ? sel : row != null ? new[] { row } : sel;
        }

        private void LayerColor_Click(object sender, RoutedEventArgs e)
        {
            var t = Targets(sender); if (t.Length == 0) return;
            var c = PickAci(t[0].LayerColorAci, false);
            if (!c.HasValue) return;
            foreach (var r in t) r.LayerColorAci = c.Value;
            Schedule();
        }

        private void HatchColor_Click(object sender, RoutedEventArgs e)
        {
            var t = Targets(sender); if (t.Length == 0) return;
            var c = PickAci(t[0].HatchColorAci, true);
            if (!c.HasValue) return;
            foreach (var r in t) r.HatchColorAci = c.Value;
            Schedule();
        }

        private void EntityColor_Click(object sender, RoutedEventArgs e)
        {
            var t = Targets(sender); if (t.Length == 0) return;
            var dlg = new AcColorDialog { IncludeByBlockByLayer = true };
            dlg.Color = t[0].TrueColor >= 0
                ? AcColor.FromRgb((byte)((t[0].TrueColor >> 16) & 255), (byte)((t[0].TrueColor >> 8) & 255), (byte)(t[0].TrueColor & 255))
                : t[0].ColorAci == 256 ? AcColor.FromColorIndex(ColorMethod.ByLayer, 256) : AcColor.FromColorIndex(ColorMethod.ByAci, t[0].ColorAci);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var c = dlg.Color;
            foreach (var r in t)
            {
                if (c.IsByLayer || c.IsByBlock) { r.TrueColor = -1; r.ColorAci = 256; }
                else if (c.ColorMethod == ColorMethod.ByColor) { r.TrueColor = (c.Red << 16) | (c.Green << 8) | c.Blue; }
                else { r.TrueColor = -1; r.ColorAci = c.ColorIndex; }
            }
            Schedule();
        }

        /// <summary>AutoCAD's colour dialog restricted to the ACI tab; ByLayer → 256 when allowed.</summary>
        private static short? PickAci(short current, bool allowByLayer)
        {
            var dlg = new AcColorDialog { IncludeByBlockByLayer = allowByLayer };
            dlg.SetDialogTabs(AcColorDialog.ColorTabs.ACITab);
            dlg.Color = current >= 1 && current <= 255 ? AcColor.FromColorIndex(ColorMethod.ByAci, current) : AcColor.FromColorIndex(ColorMethod.ByLayer, 256);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            var c = dlg.Color;
            if (c.IsByLayer || c.IsByBlock) return allowByLayer ? (short)256 : (short?)null;
            return c.ColorIndex;
        }

        // ── Preview navigation ───────────────────────────────────────────────
        private void PrevStation_Click(object sender, RoutedEventArgs e) => _vm.StepStation(-1);
        private void NextStation_Click(object sender, RoutedEventArgs e) => _vm.StepStation(+1);

        // ── Actions ──────────────────────────────────────────────────────────
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var last = new MenuItem { Header = "Son çizimi sil", IsEnabled = !string.IsNullOrEmpty(LastRunId) };
            last.Click += (s, a) => DoClear(LastRunId);
            var all = new MenuItem { Header = "Tüm kesitleri sil (UT_CROSS_SECTIONS ile çizilen)" };
            all.Click += (s, a) =>
            {
                if (MessageBox.Show(this, "Bu çizimdeki TÜM En Kesit çizimleri silinsin mi?", "Kesitleri Sil",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) DoClear("");
            };
            menu.Items.Add(last); menu.Items.Add(all);
            menu.PlacementTarget = sender as UIElement;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }

        private void DoClear(string runId)
        {
            if (ClearHandler == null) return;
            int n = ClearHandler(runId);
            if (string.IsNullOrEmpty(runId) || runId == LastRunId) { _vm.HasLastRun = false; _vm.ReplaceLast = false; }
            MessageBox.Show(this, $"{n} nesne silindi.", "Kesitleri Sil", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Result = PanelResult.Close; Close(); }

        /// <summary>Commits any pending text edit (LostFocus bindings) before the options are read.</summary>
        private void CommitEdits()
            => (System.Windows.Input.Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        private void PickSingle_Click(object sender, RoutedEventArgs e) { CommitEdits(); Result = PanelResult.PickSingle; Close(); }

        private void Excel_Click(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            _vm.CommitScope();
            if (_vm.SectionCount == 0)
            {
                MessageBox.Show(this, "Aktarılacak kesit yok — Kapsam sekmesinde en az bir boru seçin.", "En Kesit",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx", Title = "Kesit alanları — Excel'e aktar",
                FileName = "EnKesit_Alanlari_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                var cuts = _vm.Session.BuildCuts(_vm.Run);
                SectionExcelExport.Write(dlg.FileName, cuts, _vm.Style,
                    "En Kesit alanları — " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + " · " + _vm.HeaderText);
                Cursor = null;
                string extra = _vm.Session.FailedCuts.Count > 0 ? $"\n⚠ {_vm.Session.FailedCuts.Count} kesit üretilemedi." : "";
                if (MessageBox.Show(this, $"{cuts.Count} kesit yazıldı:\n{dlg.FileName}{extra}\n\nDosya açılsın mı?", "Excel'e aktar",
                                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(dlg.FileName);
            }
            catch (Exception ex)
            {
                Cursor = null;
                MessageBox.Show(this, ex.Message, "Excel'e aktarılamadı", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Draw_Click(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            _vm.CommitScope();
            if (_vm.SectionCount == 0)
            {
                MessageBox.Show(this, "Çizilecek kesit yok — Kapsam sekmesinde en az bir boru seçin.", "En Kesit",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Result = PanelResult.Draw;
            Close();
        }
    }

    // ── Converters ───────────────────────────────────────────────────────────

    public sealed class EnumBoolConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => value is bool b && b ? Enum.Parse(t, p.ToString()) : Binding.DoNothing;
    }

    public sealed class AciBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            short aci = value is short s ? s : value is int i ? (short)i : (short)7;
            if (aci < 1 || aci > 255) return new SolidColorBrush(Colors.Transparent);
            if (aci == 7) return new SolidColorBrush(Colors.Black);
            return new SolidColorBrush(SectionPreview.Aci(aci));
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }

    /// <summary>Minimal single-line input dialog.</summary>
    internal static class TextPrompt
    {
        public static string Ask(Window owner, string title, string label, string initial)
        {
            var w = new Window
            {
                Title = title, Owner = owner, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = new FontFamily("Segoe UI")
            };
            var tb = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 4, 0, 10) };
            var ok = new Button { Content = "Tamam", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "İptal", IsCancel = true, Width = 80 };
            string result = null;
            ok.Click += (s, e) => { result = tb.Text; w.DialogResult = true; };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = label });
            root.Children.Add(tb);
            root.Children.Add(buttons);
            w.Content = root;
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? result : null;
        }
    }
}
