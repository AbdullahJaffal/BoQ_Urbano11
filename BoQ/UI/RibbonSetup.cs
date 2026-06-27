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
        private const string BTN_MH_CAT      = "UM_BTN_MH_CATALOG";
        private const string BTN_MH_ASSIGN   = "UM_BTN_MH_ASSIGN";
        private const string BTN_SMART_ASSEM  = "UM_BTN_SMART_ASSEMBLY";
        private const string BTN_PIPE_CAT        = "UM_BTN_PIPE_CATALOG";
        private const string BTN_PIPE_TRENCH     = "UM_BTN_PIPE_TRENCH";
        private const string BTN_SOIL_CAT        = "UM_BTN_SOIL_CATALOG";

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

            // ── Manhole Excavation Catalog button ───────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_MH_CAT,
                Text           = "Kazı\nKuralları",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_manhole.png"),
                CommandHandler = new RibbonCommandRelay("MANHOLE_EXCAV_CATALOG"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Manhole Kazı Kuralları Kataloğu",
                    Content = "Taban çapı aralığına göre kazı kurallarını yönetir.\n" +
                              "Her kural: derinlik kademeleri, çalışma genişliği,\n" +
                              "şev, iksa, basamaklı kazı, alt temel ve geri dolgu katmanları."
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

            // ── Smart Assembly button ────────────────────────────────────────
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
                    Title   = "Akıllı Baca Montaj Sistemi",
                    Content = "Akıllı Baca Montaj ve Metraj Sistemi.\n" +
                              "• Ana Bileşen Deposu: ön döküm parça kataloğu.\n" +
                              "• Ana Kural Matrisi: boru Ø / derinlik → taban seçim matrisi.\n" +
                              "• Proje Kurulumu: DWG'ye özgü kural geçersiz kılmaları.\n" +
                              "Hesaplama algoritması bir sonraki fazda eklenecektir."
                }
            });

            // ── Pipe Catalog button ──────────────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_PIPE_CAT,
                Text           = "Boru\nKataloğu",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_pipe_catalog.png"),
                CommandHandler = new RibbonCommandRelay("PIPE_CATALOG"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Boru Kataloğu Yöneticisi",
                    Content = "Boru ailelerini ve çap tanımlarını (DN/OD/ID/Et) yönetir.\n" +
                              "• Mod A: Yerel XML ile içe/dışa aktarım.\n" +
                              "• Mod B: Urbano XML dosyasından çözümleme.\n" +
                              "• Mod C: Canlı DWG'den ARS_EXPORT_XML ile çıkarma.\n" +
                              "Katalog, Akıllı Baca Montaj boru aralığı kurallarıyla paylaşılır."
                }
            });

            // ── Pipe Trench Catalog button ───────────────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_PIPE_TRENCH,
                Text           = "Hendek\nKataloğu",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_pipe_trench.png"),
                CommandHandler = new RibbonCommandRelay("PIPE_TRENCH_CATALOG"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Boru Hendek Kuralları Kataloğu",
                    Content = "Boru çapı aralığına ve derinlik kademesine göre hendek\n" +
                              "geometrisini (genişlik boşluğu, şev, iksa) tanımlar.\n" +
                              "Her kademe altında yataklama ve geri dolgu katman yığınları\n" +
                              "ayrı ayrı yapılandırılabilir. Kurallar XML olarak kaydedilir."
                }
            });

            // ── Soil Classification Catalog button ───────────────────────────
            panelSource.Items.Add(new RibbonButton
            {
                Id             = BTN_SOIL_CAT,
                Text           = "Zemin\nKataloğu",
                ShowText       = true,
                Size           = RibbonItemSize.Large,
                LargeImage     = LoadIcon("um_soil_catalog.png"),
                CommandHandler = new RibbonCommandRelay("SOIL_CATALOG"),
                ToolTip        = new RibbonToolTip
                {
                    Title   = "Zemin Sınıfı Kataloğu",
                    Content = "Zemin türlerini ve kabarma katsayılarını yönetir.\n" +
                              "Her kayıt: zemin adı, kabarma katsayısı (örn. 1.20)\n" +
                              "ve poz numarası. Katalog XML olarak kaydedilir."
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
