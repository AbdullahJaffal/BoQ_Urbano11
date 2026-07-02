using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// "Urbano Tools" sekmesine "METRAJ" panelini ekler.
    ///
    /// Yükleme sırası bağımsız: UrbanoLock sekmeyi daha önce oluşturduysa
    /// mevcut sekmeye eklenir; aksi hâlde sekme bu plugin tarafından kurulur.
    /// Koruma panel düzeyindedir — NETLOAD yeniden yüklemelerinde çoğalmaz.
    /// </summary>
    public static class RibbonSetup
    {
        // UrbanoLock'un RibbonSetup.TAB_ID sabiti ile AYNI olmalı.
        internal const string TAB_ID = "URBANO_TOOLS_TAB";

        private const string PANEL_ID       = "UM_PANEL_METRAJ";
        private const string BUTTON_ID     = "UM_BTN_BOQ";
        private const string BTN_VIEW      = "UM_BTN_BOQ_VIEW";
        private const string BTN_SOLIDS    = "UM_BTN_SOLIDS";
        private const string BTN_MH_ASSIGN   = "UM_BTN_MH_ASSIGN";
        private const string BTN_SMART_ASSEM  = "UM_BTN_SMART_ASSEMBLY";
        private const string BTN_NETWORK_PANEL   = "UM_BTN_NETWORK_PANEL";

        public static void Initialize()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // ── Ortak sekmeyi al veya oluştur ────────────────────────────────
            RibbonTab tab = ribbon.FindTab(TAB_ID);
            if (tab == null)
            {
                tab = new RibbonTab { Title = "Urbano Tools", Id = TAB_ID };
                ribbon.Tabs.Add(tab);
            }

            // ── Çoklama koruması ─────────────────────────────────────────────
            if (tab.FindPanel(PANEL_ID) != null) return;

            // ── Panel ────────────────────────────────────────────────────────
            var panelSource = new RibbonPanelSource
            {
                Title = "Metraj",
                Id    = PANEL_ID
            };
            tab.Panels.Add(new RibbonPanel { Source = panelSource });

            // ── Tek buton: BoQ penceresi (Metraj) ────────────────────────────
            // Yeni mimari: tüm işlemler (veri güncelleme, hesaplama yöntemi
            // seçimi, tablolar ve 3B katı üretimi) bu pencerenin içindedir.
            // Eski "Metraj Çıkar" ve "3B Katı" ribbon butonları kaldırıldı.
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_VIEW,
                Text           = "Metraj",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_view.png"),
                CommandHandler = new RibbonCommandRelay("URBANO_BOQ_VIEW"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Metraj (BoQ)",
                    Content = "Metraj penceresini açar.\n" +
                              "Pencere içinden: verileri güncelle (Metraj Verisi\n" +
                              "Güncelle), Kazı/Dolgu hesap yöntemini seç, Hesapla ile\n" +
                              "tabloları doldur, Excel'e aktar ve 3B katı üret."
                }
            });

            // ── Manhole Assign button ────────────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_MH_ASSIGN,
                Text           = "Katalog\nAta",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_assign.png"),
                CommandHandler = new RibbonCommandRelay("MANHOLE_ASSIGN"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Bacaya Katalog Ata",
                    Content = "Seçilen baca entity'lerine (AG_GUID) bir kazı kataloğu\n" +
                              "grubu atar ve eşleşmeyi DWG'ye kaydeder.\n" +
                              "Bu eşleşme BoQ hesabında XML verileriyle ilişkilendirilir."
                }
            });

            // ── Akıllı Montaj button ──────────────────────────────────────────
            // Tüm katalog pencereleri (Baca Kazı, Baca Parça, Baca-Boru Bağlantı
            // Kuralları, Boru, Hendek, Zemin, Dolgu) tek bir çok sekmeli pencerede
            // toplanmıştır.
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_SMART_ASSEM,
                Text           = "Akıllı\nMontaj",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_smart_assembly.png"),
                CommandHandler = new RibbonCommandRelay("SMART_ASSEMBLY"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Akıllı Montaj",
                    Content = "Tüm katalogları tek pencerede toplayan çok sekmeli sistem:\n" +
                              "• Baca Parça Kataloğu: ön döküm parça kataloğu.\n" +
                              "• Baca-Boru Bağlantı Kuralları: boru Ø / derinlik → taban seçim matrisi.\n" +
                              "• Proje Kurulumu: DWG'ye özgü kural geçersiz kılmaları.\n" +
                              "• Baca Kazı Kataloğu, Boru Kataloğu, Hendek Kataloğu,\n" +
                              "  Zemin Kataloğu, Dolgu Kataloğu."
                }
            });

            // ── Network Panel button ─────────────────────────────────────────
            // UrbanoLock'un UT_NETWORK_PANEL penceresiyle aynı paleti açar/paylaşır
            // (bkz. NetworkPaletteSet + UrbanoLockBridge). UrbanoLock yüklüyse onun
            // paletini gösterir; değilse bu eklentinin kendi kopyasını oluşturur.
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_NETWORK_PANEL,
                Text           = "Ağ\nPaneli",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_network_panel.png"),
                CommandHandler = new RibbonCommandRelay("URBANO_NETWORK_PANEL"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Ağ Seçim Paneli",
                    Content = "Çizimdeki ağları listeler; her ağ için Aktif ve\n" +
                              "Görünürlük durumunu ayrı ayrı kontrol eder.\n" +
                              "UrbanoLock kuruluysa aynı paleti paylaşır."
                }
            });

            // İlk yüklemede sekmeyi ön plana getir.
            tab.IsActive = true;
        }

        // ── İkon yükleyici ────────────────────────────────────────────────────
        // UrbanoMetraj.dll yanındaki icons\ klasöründen yükler.
        // Dosya bulunamazsa null döner; buton ikonsuz çalışmaya devam eder.

        private static ImageSource LoadIcon(string filename)
        {
            try
            {
                string dir  = Path.GetDirectoryName(
                                  Assembly.GetExecutingAssembly().Location) ?? "";
                string path = Path.Combine(dir, "icons", filename);
                if (!File.Exists(path)) return null;

                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource         = new Uri(path, UriKind.Absolute);
                img.CacheOption       = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth  = 32;
                img.DecodePixelHeight = 32;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }

    // ── Ribbon tıklamasını AutoCAD komut kuyruğuna ileten yönlendirici ────────

    internal sealed class RibbonCommandRelay : System.Windows.Input.ICommand
    {
        private readonly string _command;

        internal RibbonCommandRelay(string command) { _command = command + "\n"; }

        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            var dm = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            dm.MdiActiveDocument?.SendStringToExecute(_command, true, false, true);
        }
    }
}
