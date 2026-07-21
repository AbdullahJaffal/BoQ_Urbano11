using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Bricscad.ApplicationServices;
using AcApp = Bricscad.ApplicationServices.Application;

namespace UrbanoMetraj.BoQ.UI.NetworkPanel
{
    /// <summary>
    /// Visual clone of UrbanoLock.UI.NetworkPaletteControl, used only when
    /// UrbanoLock's own palette isn't available in the current session (see
    /// <see cref="UrbanoLockBridge"/>). Same dark-theme layout, colours and
    /// row behaviour; backed by <see cref="NetworkSessionManager"/> instead of
    /// UrbanoLock's session manager, and with no dependency on UrbanoLock's
    /// scrape/vault/native-ComboBox-sync features.
    ///
    ///   ┌─ Header ────────────────────────────────────────────┐
    ///   │  URBANO — AĞ SEÇİMİ                [↻ Yenile]       │
    ///   ├─ Column headers ─────────────────────────────────────│
    ///   │  Ağ Adı                            Aktif   Görün    │
    ///   ├──────────────────────────────────────────────────────│
    ///   │  [TÜMÜ]                            [☑]     [◉]      │
    ///   │  ET1_YSU                           [☑]     [◉]      │
    ///   │  ET2_ASU                           [☐]     [○]      │
    ///   └──────────────────────────────────────────────────────┘
    /// </summary>
    public class NetworkPaletteControl : UserControl
    {
        // ── Theme brushes ─────────────────────────────────────────────────────

        static readonly SolidColorBrush BgBrush        = Brush("#1E1E1E");
        static readonly SolidColorBrush PanelBrush     = Brush("#252526");
        static readonly SolidColorBrush HeaderBrush    = Brush("#2D2D2D");
        static readonly SolidColorBrush SeparatorBrush = Brush("#3C3C3C");
        static readonly SolidColorBrush FgBrush        = Brush("#CCCCCC");
        static readonly SolidColorBrush DimBrush       = Brush("#888888");
        static readonly SolidColorBrush AccentBrush    = Brush("#007ACC");
        static readonly SolidColorBrush EyeOnBrush     = Brush("#4EC9B0");
        static readonly SolidColorBrush EyeOffBrush    = Brush("#555555");
        static readonly SolidColorBrush HoverBrush     = Brush("#3E3E3E");
        static readonly SolidColorBrush RowAltBrush    = Brush("#2A2A2A");
        static readonly SolidColorBrush MasterBrush    = Brush("#2D5060");

        static SolidColorBrush Brush(string hex)
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // ── State ─────────────────────────────────────────────────────────────

        private List<string> _networks = new List<string>();
        private StackPanel   _rows;
        private TextBlock    _emptyLabel;
        private ScrollViewer _scroll;

        // ── Construction ──────────────────────────────────────────────────────

        public NetworkPaletteControl()
        {
            Background = BgBrush;
            BuildLayout();
            NetworkSessionManager.StateChanged += (s, e) => RefreshRows();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Updates the control with a fresh list of network IDs from the drawing.
        /// Preserves existing session state (checked/eye) for networks that still exist.
        /// </summary>
        public void LoadNetworks(List<string> networks)
        {
            _networks = networks ?? new List<string>();
            RefreshRows();
        }

        // ── Layout construction ───────────────────────────────────────────────

        private void BuildLayout()
        {
            var root = new DockPanel { Background = BgBrush, LastChildFill = true };

            // ── Top bar ───────────────────────────────────────────────────────
            var topBar = new DockPanel
            {
                Background = HeaderBrush,
                Height     = 32,
                Margin     = new Thickness(0, 0, 0, 1)
            };

            var refreshBtn = MakeTextButton("↻", "Ağ listesini yenile", 28, 28);
            refreshBtn.Click += (s, e) => OnRefreshClicked();
            DockPanel.SetDock(refreshBtn, Dock.Right);
            topBar.Children.Add(refreshBtn);

            topBar.Children.Add(new TextBlock
            {
                Text               = "URBANO — AĞ SEÇİMİ",
                Foreground         = AccentBrush,
                FontWeight         = FontWeights.Bold,
                FontSize           = 11,
                VerticalAlignment  = VerticalAlignment.Center,
                Margin             = new Thickness(8, 0, 0, 0)
            });

            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            // ── Column header row ─────────────────────────────────────────────
            var colHeaders = MakeColumnHeader();
            DockPanel.SetDock(colHeaders, Dock.Top);
            root.Children.Add(colHeaders);

            // ── Separator ─────────────────────────────────────────────────────
            var sep = new Border { Height = 1, Background = SeparatorBrush };
            DockPanel.SetDock(sep, Dock.Top);
            root.Children.Add(sep);

            // ── Empty-state label ─────────────────────────────────────────────
            _emptyLabel = new TextBlock
            {
                Text              = "Bu çizimde ağ bulunamadı.",
                Foreground        = DimBrush,
                FontStyle         = FontStyles.Italic,
                FontSize          = 11,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 16, 12, 0),
                Visibility        = Visibility.Collapsed,
                TextWrapping      = TextWrapping.Wrap
            };
            DockPanel.SetDock(_emptyLabel, Dock.Top);
            root.Children.Add(_emptyLabel);

            // ── Network rows ──────────────────────────────────────────────────
            _rows  = new StackPanel { Background = BgBrush };
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content                       = _rows,
                Background                    = BgBrush
            };
            root.Children.Add(_scroll);

            Content = root;
        }

        private Grid MakeColumnHeader()
        {
            var g = new Grid
            {
                Background = PanelBrush,
                Height     = 24
            };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            AddHeaderCell(g, "Ağ Adı",   0, TextAlignment.Left,  new Thickness(8, 0, 0, 0));
            AddHeaderCell(g, "Aktif",    1, TextAlignment.Center, new Thickness(0));
            AddHeaderCell(g, "Görün.",   2, TextAlignment.Center, new Thickness(0));
            return g;
        }

        private static void AddHeaderCell(Grid g, string text, int col, TextAlignment align, Thickness margin)
        {
            var tb = new TextBlock
            {
                Text              = text,
                Foreground        = DimBrush,
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = align,
                Margin            = margin
            };
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        // ── Row generation ────────────────────────────────────────────────────

        private void RefreshRows()
        {
            _rows.Children.Clear();

            if (_networks.Count == 0)
            {
                _emptyLabel.Visibility = Visibility.Visible;
                return;
            }
            _emptyLabel.Visibility = Visibility.Collapsed;

            // ── [TÜMÜ] master row ─────────────────────────────────────────────
            bool allActive  = _networks.TrueForAll(NetworkSessionManager.IsActive);
            bool allVisible = _networks.TrueForAll(NetworkSessionManager.IsVisible);
            _rows.Children.Add(MakeRow(
                networkId:  null,
                label:      "[TÜMÜ]",
                isActive:   allActive,
                isVisible:  allVisible,
                isMaster:   true,
                rowIndex:   0));

            var masterSep = new Border { Height = 1, Background = SeparatorBrush, Margin = new Thickness(0) };
            _rows.Children.Add(masterSep);

            // ── Per-network rows ──────────────────────────────────────────────
            for (int i = 0; i < _networks.Count; i++)
            {
                string id = _networks[i];
                _rows.Children.Add(MakeRow(
                    networkId: id,
                    label:     id,
                    isActive:  NetworkSessionManager.IsActive(id),
                    isVisible: NetworkSessionManager.IsVisible(id),
                    isMaster:  false,
                    rowIndex:  i + 1));
            }
        }

        private UIElement MakeRow(
            string networkId, string label,
            bool   isActive,  bool isVisible,
            bool   isMaster,  int  rowIndex)
        {
            var bg = isMaster ? MasterBrush : (rowIndex % 2 == 0 ? PanelBrush : RowAltBrush);

            var g = new Grid
            {
                Height     = 28,
                Background = bg
            };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            // Hover effect
            g.MouseEnter += (s, e) => g.Background = HoverBrush;
            g.MouseLeave += (s, e) => g.Background = bg;

            // Network name label
            var nameLbl = new TextBlock
            {
                Text              = label,
                Foreground        = isMaster ? AccentBrush : FgBrush,
                FontWeight        = isMaster ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 4, 0),
                TextTrimming      = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameLbl, 0);
            g.Children.Add(nameLbl);

            // Active checkbox
            var activeBtn = MakeCheckToggle(isActive);
            Grid.SetColumn(activeBtn, 1);
            activeBtn.Checked   += (s, e) =>
            {
                if (networkId == null) NetworkSessionManager.SetAllActive(_networks, true);
                else                   NetworkSessionManager.SetActive(networkId, true);
            };
            activeBtn.Unchecked += (s, e) =>
            {
                if (networkId == null) NetworkSessionManager.SetAllActive(_networks, false);
                else                   NetworkSessionManager.SetActive(networkId, false);
            };
            g.Children.Add(activeBtn);

            // Visibility eye toggle
            var eyeBtn = MakeEyeToggle(isVisible);
            Grid.SetColumn(eyeBtn, 2);
            eyeBtn.Checked   += (s, e) =>
            {
                if (networkId == null) NetworkSessionManager.SetAllVisible(_networks, true);
                else                   NetworkSessionManager.SetVisible(networkId, true);
            };
            eyeBtn.Unchecked += (s, e) =>
            {
                if (networkId == null) NetworkSessionManager.SetAllVisible(_networks, false);
                else                   NetworkSessionManager.SetVisible(networkId, false);
            };
            g.Children.Add(eyeBtn);

            return g;
        }

        // ── Control factories ─────────────────────────────────────────────────

        private ToggleButton MakeCheckToggle(bool isChecked)
        {
            var btn = new ToggleButton
            {
                IsChecked         = isChecked,
                Width             = 22,
                Height            = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                Background        = Brushes.Transparent,
                BorderBrush       = SeparatorBrush,
                BorderThickness   = new Thickness(1),
                ToolTip           = "Aktif — bu ağ seçili mi?",
            };

            btn.Content = isChecked ? "✓" : "  ";
            btn.Foreground = isChecked ? AccentBrush : DimBrush;

            btn.Checked   += (s, e) => { btn.Content = "✓"; btn.Foreground = AccentBrush; };
            btn.Unchecked += (s, e) => { btn.Content = "  "; btn.Foreground = DimBrush; };

            btn.Template = BuildToggleBtnTemplate();
            return btn;
        }

        private ToggleButton MakeEyeToggle(bool isVisible)
        {
            var btn = new ToggleButton
            {
                IsChecked           = isVisible,
                Width               = 22,
                Height              = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Cursor              = Cursors.Hand,
                Background          = Brushes.Transparent,
                BorderBrush         = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                ToolTip             = "Görünürlük — bu ağın katmanlarını aç/kapat",
                FontSize            = 14
            };

            btn.Content    = isVisible ? "◉" : "○";
            btn.Foreground = isVisible ? EyeOnBrush : EyeOffBrush;

            btn.Checked   += (s, e) => { btn.Content = "◉"; btn.Foreground = EyeOnBrush; };
            btn.Unchecked += (s, e) => { btn.Content = "○"; btn.Foreground = EyeOffBrush; };

            btn.Template = BuildToggleBtnTemplate();
            return btn;
        }

        /// <summary>
        /// Minimal ControlTemplate for ToggleButton: transparent background with
        /// content area, a subtle border-color shift on hover/press for dark theme.
        /// </summary>
        private static ControlTemplate BuildToggleBtnTemplate()
        {
            var tpl = new ControlTemplate(typeof(ToggleButton));

            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);

            borderFactory.AppendChild(cpFactory);
            tpl.VisualTree = borderFactory;

            tpl.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value    = true
            });

            return tpl;
        }

        private Button MakeTextButton(string text, string tooltip, double w, double h)
        {
            return new Button
            {
                Content     = text,
                ToolTip     = tooltip,
                Width       = w,
                Height      = h,
                FontSize    = 14,
                Background  = Brushes.Transparent,
                Foreground  = FgBrush,
                BorderBrush = Brushes.Transparent,
                Cursor      = Cursors.Hand,
                Margin      = new Thickness(2),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        // ── Refresh handler ───────────────────────────────────────────────────

        /// <summary>
        /// UrbanoMetraj has no UI-automation scraper of its own — the network list
        /// lives in the shared URBANO_NETWORKS NOD entry that UrbanoLock's scrape
        /// writes. Refresh here just re-reads that entry and reloads session state;
        /// it cannot discover networks UrbanoLock has never scraped.
        /// </summary>
        private void OnRefreshClicked()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var networks = NetworkSessionManager.GetAllNetworks(doc.Database);
            NetworkSessionManager.InitializeFromDrawing(doc.Database);
            LoadNetworks(networks);

            if (networks.Count == 0)
            {
                ed?.WriteMessage(
                    "\n[UrbanoMetraj] Ağ listesi bulunamadı.\n" +
                    "[UrbanoMetraj] Ağları taramak için UrbanoLock'un UT_NETWORK_PANEL\n" +
                    "[UrbanoMetraj] penceresindeki 'Yenile' butonunu kullanın.\n");
            }
            else
            {
                ed?.WriteMessage($"\n[UrbanoMetraj] {networks.Count} ağ yüklendi.\n");
            }
        }
    }
}
