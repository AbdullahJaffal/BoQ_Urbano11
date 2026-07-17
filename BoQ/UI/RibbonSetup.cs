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
        private const string BTN_BACA_KESIF  = "UM_BTN_BACA_KESIF";
        private const string BTN_METRAJ_KESIF = "UM_BTN_METRAJ_KESIF";
        private const string BTN_SMART_ASSEM  = "UM_BTN_SMART_ASSEMBLY";
        private const string BTN_PROJ_SETTINGS  = "UM_BTN_PROJE_AYARLARI";
        private const string BTN_NETWORK_PANEL   = "UM_BTN_NETWORK_PANEL";

        public static void Initialize()
        {
            // ── Suite merge (2026-07-18): DISABLED — UrbanoLock now builds the
            //    "Metraj" ribbon panel from its own RibbonLayout/CommandCatalog
            //    (single source, controllable via UT_COMMANDS). Building a panel
            //    here too would duplicate the buttons on the shared "Urbano Tools"
            //    tab. The commands themselves are unaffected (auto-scanned) and the
            //    body below is kept for reference / standalone-Metraj fallback.
            return;
#pragma warning disable CS0162 // unreachable code (intentional — see note above)
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
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_view.png") ?? RibbonIcons.Metraj(),
                CommandHandler = new RibbonCommandRelay("UT_BOQ_VIEW"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Metraj (BoQ)",
                    Content = "Metraj penceresini açar. Pencere içinden sırasıyla:\n" +
                              "1) Metraj Verisi Güncelle: Urbano'dan veri çek.\n" +
                              "2) Akıllı Montaj'da Tür Eşleştirme / Baca-Boru\n" +
                              "   Bağlantı Kuralları ile eşleştirmeleri tamamla.\n" +
                              "3) Hesapla: geometri + Baca AI hesabını çalıştır.\n" +
                              "4) Sonuçları Göster ile Kazı/Dolgu yöntemini\n" +
                              "   değiştirip tabloları yenile, Excel'e aktar,\n" +
                              "   3B katı üret."
                }
            });

            // ── [GİZLENDİ 2026-07-09] Katalog Ata + Baca Keşif Tablosu ───────────
            // Bu iki buton kullanıcı isteğiyle ribbon'dan gizlendi. Komutlar hâlâ
            // tanımlı ve komut satırından çalışır: UT_MANHOLE_ASSIGN ve
            // UT_BACA_KESIF_TABLOSU. Butonları tekrar göstermek için aşağıdaki
            // "#if false" satırını "#if true" yapmak yeterli.
#if false
            // ── Manhole Assign button ────────────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_MH_ASSIGN,
                Text           = "Katalog\nAta",
                ShowText       = true,
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_assign.png") ?? RibbonIcons.KatalogAta(),
                CommandHandler = new RibbonCommandRelay("UT_MANHOLE_ASSIGN"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Bacaya Katalog Ata",
                    Content = "Seçilen baca entity'lerine (AG_GUID) bir kazı kataloğu\n" +
                              "grubu atar ve eşleşmeyi DWG'ye kaydeder.\n" +
                              "Bu eşleşme BoQ hesabında XML verileriyle ilişkilendirilir."
                }
            });

            // ── Baca Keşif Tablosu button ─────────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_BACA_KESIF,
                Text           = "Baca Keşif\nTablosu",
                ShowText       = true,
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_baca_kesif.png") ?? RibbonIcons.BacaKesif(),
                CommandHandler = new RibbonCommandRelay("UT_BACA_KESIF_TABLOSU"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Baca Keşif Tablosu",
                    Content = "UT_BOQ_HESAPLA ile kaydedilmiş BoQ verisinden,\n" +
                              "her baca için bir satır olacak şekilde ayrı bir\n" +
                              "Excel keşif tablosu (giriş/çıkış boru, kot, derinlik,\n" +
                              "kazı hacmi, parça dökümü) üretir."
                }
            });
#endif

            // ── Metraj Keşif Tablosu button ───────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_METRAJ_KESIF,
                Text           = "Metraj Keşif\nTablosu",
                ShowText       = true,
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_metraj_kesif.png") ?? RibbonIcons.MetrajKesif(),
                CommandHandler = new RibbonCommandRelay("UT_METRAJ_KESIF_TABLOSU"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Metraj Keşif Tablosu",
                    Content = "UT_BOQ_HESAPLA ile kaydedilmiş BoQ verisinden,\n" +
                              "sayfalara bölünmüş bir metraj keşif tablosu üretir:\n" +
                              "1. sayfa proje bilgileri (elle doldurulur), ardından\n" +
                              "her aktif ağ için ayrı sayfa — kazı/dolgu (zemin ve\n" +
                              "dolgu kataloğu poz no'ları), boru boyları ve baca\n" +
                              "parçaları; Br. Fiyat elle girilir, Tutar formülle hesaplanır."
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
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_smart_assembly.png") ?? RibbonIcons.AkilliMontaj(),
                CommandHandler = new RibbonCommandRelay("UT_SMART_ASSEMBLY"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Akıllı Montaj",
                    Content = "Katalogları ve kuralları tek pencerede toplayan çok sekmeli sistem:\n" +
                              "• Prefabrik Baca Kataloğu, Boru Kataloğu, Kazı Tipi\n" +
                              "  Kataloğu, Dolgu Kataloğu.\n" +
                              "• Baca Seçim Kuralları: boru Ø / derinlik → taban seçim matrisi.\n" +
                              "• Baca Kazı Kuralları, Boru Hendek Kuralları.\n" +
                              "Proje Kurulumu ve Tür Eşleştirme artık ayrı \"Proje Ayarları\" penceresindedir."
                }
            });

            // ── Proje Ayarları button ─────────────────────────────────────────
            // Proje Kurulumu (DWG) + Tür Eşleştirme; Akıllı Montaj penceresiyle
            // aynı ViewModel'i paylaşan ayrı modeless pencere.
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_PROJ_SETTINGS,
                Text           = "Proje\nAyarları",
                ShowText       = true,
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_proje_ayarlari.png") ?? RibbonIcons.ProjeAyarlari(),
                CommandHandler = new RibbonCommandRelay("UT_PROJE_AYARLARI"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Proje Ayarları",
                    Content = "DWG'ye özgü ayarlar:\n" +
                              "• Proje Kurulumu: bu çizime özgü kural geçersiz kılmaları.\n" +
                              "• Tür Eşleştirme: Urbano katalog öğelerini kendi\n" +
                              "  kataloglarımıza bağlar."
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
                ShowImage      = true,
                Size           = RibbonItemSize.Large,
                // Dikey yönelim: etiketi ikonun ALTINA ortalar (UrbanoLock ile
                // birebir aynı "büyük buton" görünümü). Ayarlanmazsa metin ikonun
                // yanında/farklı konumda kalabilir.
                Orientation    = System.Windows.Controls.Orientation.Vertical,
                LargeImage     = LoadIcon("um_network_panel.png") ?? RibbonIcons.AgPaneli(),
                CommandHandler = new RibbonCommandRelay("UT_NET_PANEL"),
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
#pragma warning restore CS0162
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
