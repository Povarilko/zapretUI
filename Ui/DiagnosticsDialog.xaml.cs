using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ZapretUi
{
    public partial class DiagnosticsDialog : Window
    {
        readonly FontFamily _iconFont;

        DiagnosticsDialog()
        {
            InitializeComponent();
            DialogBase.Init(this);
            if (Ctx.Main != null) Owner = Ctx.Main;
            _iconFont = (FontFamily)FindResource("IconFont");

            BtnRecheck.Click += (s, e) => Safe.Run(Run);
            BtnConflicts.Click += (s, e) => Safe.Run(() =>
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                try { Append(Diagnostics.RemoveConflicts(Ctx.DiagConflicts)); }
                finally { Cursor = null; }
                Ctx.DiagConflicts = new List<string> { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" }.FindAll(Proc.ServiceExists);
                if (Ctx.DiagConflicts.Count == 0) ConflictBox.Visibility = Visibility.Collapsed;
            });
            BtnDiscord.Click += (s, e) => Safe.Run(() =>
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                try { Append(Diagnostics.ClearDiscordCache()); }
                finally { Cursor = null; }
            });
        }

        void Append(List<DiagItem> items)
        {
            foreach (var it in items) Rows.Children.Add(NewDiagRow(it));
            RowsScroll.ScrollToEnd();
        }

        Grid NewDiagRow(DiagItem item)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = new TextBlock { FontFamily = _iconFont, FontSize = 14, Margin = new Thickness(0, 2, 0, 0) };
            switch (item.Level)
            {
                case "good": ic.Text = "\uE930"; ic.Foreground = Theme.Pal["Good"]; break;
                case "mid": ic.Text = "\uE7BA"; ic.Foreground = Theme.Pal["Mid"]; break;
                default: ic.Text = "\uEA39"; ic.Foreground = Theme.Pal["Bad"]; break;
            }
            g.Children.Add(ic);
            var sp = new StackPanel();
            Grid.SetColumn(sp, 1);
            var t = Ui.Text(item.Title, 13);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(item.Detail))
            {
                var det = Ui.Text(item.Detail, 12, "Normal", Theme.Pal["Muted"]);
                det.TextWrapping = TextWrapping.Wrap;
                det.TextTrimming = TextTrimming.None;
                sp.Children.Add(det);
            }
            if (!string.IsNullOrEmpty(item.Link))
            {
                var lt = new TextBlock { FontSize = 12 };
                var h = new Hyperlink(new Run(item.Link)) { Foreground = Theme.Pal["Accent"] };
                string url = item.Link;
                h.Click += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                lt.Inlines.Add(h);
                sp.Children.Add(lt);
            }
            g.Children.Add(sp);
            return g;
        }

        void Run()
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                Rows.Children.Clear();
                var items = Diagnostics.GetResults();
                Append(items);
                RowsScroll.ScrollToHome();
                int bad = items.Where(i => i.Level == "bad").Count();
                int mid = items.Where(i => i.Level == "mid").Count();
                DlgSummary.Text = (bad > 0 || mid > 0 ? $"Проблем: {bad}, предупреждений: {mid}" : "Всё в порядке") + "\nПапка zapret: " + Ctx.Root;
                ConflictBox.Visibility = Ctx.DiagConflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtConflicts.Text = string.Join(", ", Ctx.DiagConflicts) + " — службы будут остановлены и удалены, вместе с драйвером WinDivert.";
                int found = Diagnostics.DiscordInstallCount;
                BtnDiscord.IsEnabled = found > 0;
                TxtDiscord.Text = found > 0
                    ? "Найдено: " + Diagnostics.DiscordInstallNames + ". Запущенный Discord закроется, удалятся папки Cache, Code Cache и GPUCache — вход и настройки останутся. Помогает, если Discord не грузится после включения zapret."
                    : "Discord не найден.";
            }
            finally { Cursor = null; }
        }

        public static void Show(Window owner)
        {
            var d = new DiagnosticsDialog();
            if (owner != null) d.Owner = owner;
            // Первый прогон — под пеленой busy главного окна
            if (Ctx.Main != null) Ctx.Main.Busy("Проверяю систему: службы, прокси, DNS, WinDivert", d.Run);
            else d.Run();
            d.ShowDialog();
        }
    }
}
