using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ZapretUi
{
    public partial class MainWindow
    {
        DispatcherTimer _tgTimer;

        void WireTg()
        {
            // Проверка последней версии и скачивание идут в фоне; этот таймер забирает результат и двигает полосу
            _tgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _tgTimer.Tick += (s, e) =>
            {
                try
                {
                    if (TgProxy.UpdateCheck()) UpdateTgTab();
                    UpdateTgDownload();
                }
                catch (Exception ex)
                {
                    TgProxy.Download = null;
                    MessageDialog.ShowError(ex.Message);
                }
                if (TgProxy.CheckTask == null && TgProxy.Download == null) _tgTimer.Stop();
            };

            // SelectionChanged всплывает и от списков внутри вкладок — берём только смену самой вкладки
            Tabs.SelectionChanged += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, Tabs) || !ReferenceEquals(Tabs.SelectedItem, TabTg)) return;
                Safe.Run(() =>
                {
                    if (TgProxy.Latest == null && TgProxy.CheckTask == null) { TgProxy.StartCheck(); _tgTimer.Start(); }
                    UpdateTgTab();
                });
            };
            BtnTgDownload.Click += (s, e) => Safe.Run(() => { TgProxy.StartDownload(); UpdateTgTab(); });
            BtnTgCheck.Click += (s, e) => Safe.Run(() =>
            {
                TgProxy.Latest = null;
                TgProxy.StartCheck();
                _tgTimer.Start();
                UpdateTgTab();
            });
            BtnTgStart.Click += (s, e) => Safe.Run(() =>
            {
                TgProxy.StartAsUser(TgProxy.TgExe);
                Ui.Pill(TgStatePill, TgStateText, "запускается", "mid");
            });
            BtnTgPick.Click += (s, e) => Safe.Run(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Выберите скачанный TG WS Proxy",
                    Filter = "TG WS Proxy (TgWsProxy*.exe)|TgWsProxy*.exe|Программы (*.exe)|*.exe",
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                };
                if (dlg.ShowDialog(this) != true) return;
                Busy("Копирую TG WS Proxy в папку окна", () => TgProxy.InstallFile(dlg.FileName));
                UpdateTgTab();
                UpdateTgPill();
            });
            BtnTgOpenTg.Click += (s, e) => Safe.Run(() =>
            {
                string link = TgProxy.GetLink();
                if (link == null) throw new Exception("Ссылки пока нет: запустите прокси хотя бы раз.");
                TgProxy.StartAsUser(link);
            });
            BtnTgCopy.Click += (s, e) => Safe.Run(() =>
            {
                string link = TgProxy.GetLink();
                if (link == null) throw new Exception("Ссылки пока нет: запустите прокси хотя бы раз.");
                Clipboard.SetText(link);
                MessageDialog.Show("Ссылка скопирована. Отправьте её себе в Telegram (например, в «Избранное») и нажмите на неё — Telegram предложит подключить прокси.");
            });
            ChkTgAutostart.Click += (s, e) => Safe.Run(() =>
            {
                if (TgProxy.Loading) return;
                TgProxy.SetAutostart(ChkTgAutostart.IsChecked == true);
                UpdateTgTab();
            });
            // Работает копия из другой папки — закрыть её и запустить копию окна
            BtnTgSwitch.Click += (s, e) => Safe.Run(() =>
            {
                Busy("Перезапускаю TG WS Proxy из папки окна", () =>
                {
                    foreach (var p in TgProxy.GetProcesses().Where(p =>
                    {
                        try { return !string.Equals(p.MainModule?.FileName, TgProxy.TgExe, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }).ToList())
                    {
                        try { Proc.Kill(p.Id); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                    TgProxy.StartAsUser(TgProxy.TgExe);
                });
                UpdateTgTab();
            });
            BtnTgLog.Click += (s, e) => Safe.Run(() => TgProxy.StartAsUser(Path.Combine(TgProxy.TgDataDir, "proxy.log")));
            BtnTgFolder.Click += (s, e) => Safe.Run(() =>
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + TgProxy.TgDataDir + "\"") { UseShellExecute = true }));
            BtnTgReleases.Click += (s, e) => Safe.Run(() => TgProxy.StartAsUser(TgProxy.TgReleasesUrl));
        }

        void UpdateTgDownload()
        {
            var d = TgProxy.Download;
            if (d == null) return;
            long got = File.Exists(d.Tmp) ? new FileInfo(d.Tmp).Length : 0;
            TgProgressPanel.Visibility = Visibility.Visible;
            if (d.Size > 0)
            {
                TgProgress.IsIndeterminate = false;
                TgProgress.Value = Math.Min(100, 100.0 * got / d.Size);
            }
            else TgProgress.IsIndeterminate = true;
            TgProgressText.Text = d.Size > 0
                ? $"Скачиваю TG WS Proxy {d.Version} — {Math.Round(got / 1048576.0, 1)} из {Math.Round(d.Size / 1048576.0, 1)} МБ"
                : $"Скачиваю TG WS Proxy — {Math.Round(got / 1048576.0, 1)} МБ";
            if (!d.Task.IsCompleted) return;
            TgProxy.Download = null;
            TgProgressPanel.Visibility = Visibility.Collapsed;
            try
            {
                if (d.Task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    string err = d.Task.Exception?.InnerException?.Message ?? "скачивание прервано";
                    throw new Exception("Не удалось скачать TG WS Proxy: " + err);
                }
                Busy($"Устанавливаю TG WS Proxy {d.Version}", () => TgProxy.InstallFile(d.Tmp));
            }
            catch (Exception ex)
            {
                MessageDialog.Show(ex.Message + "\n\nЕсли GitHub недоступен из вашей сети, скачайте файл другим способом (например, через браузер с VPN) и используйте кнопку «Указать скачанный файл».", MessageKind.Warning);
            }
            finally { try { if (File.Exists(d.Tmp)) File.Delete(d.Tmp); } catch { } }
            UpdateTgTab();
            UpdateTgPill();
        }

        // Вкладка TG WS Proxy: статус, версии, какие кнопки нужны сейчас
        void UpdateTgTab()
        {
            var s = TgProxy.GetState();
            TgProxy.Loading = true;
            if (!s.Installed && !s.Running) Ui.Pill(TgStatePill, TgStateText, "не скачан", "na");
            else if (s.Listening) Ui.Pill(TgStatePill, TgStateText, $"работает · порт {s.Port}", "good");
            else if (s.Running) Ui.Pill(TgStatePill, TgStateText, "запускается", "mid");
            else Ui.Pill(TgStatePill, TgStateText, "не запущен", "na");

            var l = TgProxy.Latest;
            string local = TgProxy.GetVersion();
            bool newer = l != null && l.Version != null && local != null && TgProxy.TestNewer(l.Version, local);
            string latestNote = TgProxy.CheckTask != null ? "проверяю последнюю версию"
                : l == null ? ""
                : l.Error != null ? l.Error
                : newer ? $"доступна версия {l.Version}"
                : local != null ? "это последняя версия"
                : $"последняя версия — {l.Version}, {Math.Round(l.Size / 1048576.0)} МБ";
            TgVersion.Text = local != null
                ? $"Установлена версия {local}" + (latestNote.Length > 0 ? $" · {latestNote}" : "") + $"\n{TgProxy.TgExe}"
                : "Не скачан" + (latestNote.Length > 0 ? $" · {latestNote}" : "");

            bool busy = TgProxy.Download != null;
            BtnTgDownload.Visibility = local == null || newer ? Visibility.Visible : Visibility.Collapsed;
            BtnTgDownload.Content = newer ? $"Обновить до {l.Version}" : "Скачать";
            BtnTgDownload.IsEnabled = !busy && l != null && l.Url != null;
            BtnTgStart.Visibility = local != null && !s.Running ? Visibility.Visible : Visibility.Collapsed;
            BtnTgPick.Visibility = local == null ? Visibility.Visible : Visibility.Collapsed;
            BtnTgPick.IsEnabled = !busy;
            BtnTgCheck.Visibility = local != null ? Visibility.Visible : Visibility.Collapsed;
            BtnTgCheck.IsEnabled = TgProxy.CheckTask == null;
            TgNotePanel.Visibility = s.Foreign ? Visibility.Visible : Visibility.Collapsed;
            BtnTgSwitch.Visibility = local != null ? Visibility.Visible : Visibility.Collapsed;
            TgNote.Text = local != null
                ? "Сейчас работает TG WS Proxy, запущенный из другой папки. Обновляет и запускает окно свою копию — из папки выше."
                : "Сейчас работает TG WS Proxy, запущенный из другой папки. Его статус виден здесь; чтобы окно могло его обновлять, укажите этот файл или скачайте прокси.";

            string link = TgProxy.GetLink();
            BtnTgOpenTg.IsEnabled = link != null;
            BtnTgCopy.IsEnabled = link != null;
            TgConnectInfo.Text = link == null
                ? "Ссылка появится, когда прокси запустится в первый раз и создаст свои настройки."
                : !s.Running
                    ? $"Прокси MTProto 127.0.0.1:{s.Port}. Сейчас он не запущен — сначала запустите его, иначе Telegram не подключится."
                    : $"Прокси MTProto 127.0.0.1:{s.Port}. «Открыть в Telegram» добавит его в Telegram Desktop — то же, что пункт в меню трея. Если не сработало, скопируйте ссылку, отправьте её себе в «Избранное» и нажмите на неё там.";

            ChkTgAutostart.IsEnabled = local != null;
            ChkTgAutostart.IsChecked = TgProxy.TestAutostart();
            BtnTgLog.IsEnabled = File.Exists(Path.Combine(TgProxy.TgDataDir, "proxy.log"));
            BtnTgFolder.IsEnabled = Directory.Exists(TgProxy.TgDataDir);
            TgProxy.Loading = false;
        }

        // Плашка в шапке — если прокси скачан окном (exe в его папке) или просто работает
        void UpdateTgPill()
        {
            var s = TgProxy.GetState();
            if (!s.Installed && !s.Running) { PillTg.Visibility = Visibility.Collapsed; return; }
            PillTg.Visibility = Visibility.Visible;
            if (s.Listening) Ui.Pill(PillTg, PillTgText, "TG WS работает", "good");
            else if (s.Running) Ui.Pill(PillTg, PillTgText, "TG WS запускается", "mid");
            else Ui.Pill(PillTg, PillTgText, "TG WS не запущен", "na");
            PillTg.ToolTip = $"TG WS Proxy — прокси для Telegram, порт {s.Port}";
        }
    }
}
