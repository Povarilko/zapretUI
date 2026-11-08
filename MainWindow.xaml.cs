using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZapretUi
{
    public partial class MainWindow : Window
    {
        Grid _busyOverlay;
        TextBlock _busyText;
        readonly List<string> _busyStack = new List<string>();
        readonly DispatcherTimer _timer;
        int _tick;
        string _preTestManual;

        public MainWindow()
        {
            InitializeComponent();
            DialogBase.Init(this);

            Ctx.Strategies = Zapret.GetStrategies();
            TxtVersion.Text = "v" + Zapret.GetLocalVersion();
            foreach (var s in Ctx.Strategies) CbStrategy.Items.Add(s.Name);

            AddBusyOverlay();
            WireStatus();
            WireTests();
            WireResults();
            WireHistory();
            WireSettings();
            WireLists();
            WireTg();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) =>
            {
                try
                {
                    Theme.Sync();
                    UpdateStatus();
                    _tick++;
                    if (_tick % 30 == 0) ShowHistory();
                }
                catch { }
                try { CheckUpdateTick(); } catch { }
            };

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Ctx.DetailOpen && Tabs.SelectedIndex == 0) { CloseDetail(); e.Handled = true; }
            };
        }

        // ---------- busy overlay ----------

        public void Busy(string text, Action action)
        {
            if (_busyOverlay == null) { action(); return; }
            _busyStack.Add(text);
            _busyText.Text = text;
            _busyOverlay.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;
            // Приоритет Render: окно перерисуется, но щелчки, пришедшие за это время, не обработаются посреди действия
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            try { action(); }
            finally
            {
                _busyStack.RemoveAt(_busyStack.Count - 1);
                if (_busyStack.Count > 0) _busyText.Text = _busyStack[_busyStack.Count - 1];
                else { _busyOverlay.Visibility = Visibility.Collapsed; Mouse.OverrideCursor = null; }
            }
        }

        // Пелена поверх всего окна: затемнение и карточка с надписью по центру
        void AddBusyOverlay()
        {
            var content = Content;
            Content = null;
            var grid = new Grid();
            grid.Children.Add((UIElement)content);
            var ov = new Grid { Visibility = Visibility.Collapsed };
            var dim = new Border { Background = Theme.Pal["Bg"], Opacity = 0.65 };
            ov.Children.Add(dim);
            var card = new Border
            {
                Background = Theme.Pal["Surface"],
                BorderBrush = Theme.Pal["Border"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(22, 16, 22, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 340,
            };
            var row = new DockPanel();
            var icon = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 20,
                Foreground = Theme.Pal["Accent"],
                Text = "\uE895",
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(icon);
            var texts = new StackPanel();
            _busyText = Ui.Text("", 14, "SemiBold");
            _busyText.TextWrapping = TextWrapping.Wrap;
            _busyText.TextTrimming = TextTrimming.None;
            _busyText.MaxWidth = 440;
            texts.Children.Add(_busyText);
            var sub = Ui.Text("Подождите несколько секунд — окно ответит, когда закончит.", 12, "Normal", Theme.Pal["Muted"]);
            sub.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(sub);
            row.Children.Add(texts);
            card.Child = row;
            ov.Children.Add(card);
            grid.Children.Add(ov);
            Content = grid;
            _busyOverlay = ov;
        }

        // ---------- service actions (обёртки с busy, как в оригинале) ----------

        public void StartStrategy(string name)
        {
            if (!Ctx.Strategies.Any(b => b.Name == name)) throw new Exception($"Не найден файл стратегии: {name}.bat");
            if (Zapret.ServiceInstalled() && Ctx.UiState.ServiceStartAction != "auto")
            {
                var st = Zapret.GetState();
                var answer = ConfirmServiceDialog.ShowForStart(this, st.ServiceStrategy, name);
                if (!answer.Ok) return;
                if (answer.DontAsk)
                {
                    Ctx.UiState.ServiceStartAction = "auto";
                    Ctx.UiState.Save();
                    LoadSettings();
                }
            }
            Cursor = Cursors.Wait;
            try { Busy($"Запускаю {name} — ставлю службу zapret", () => Zapret.InstallZapretServiceCore(name)); }
            finally { Cursor = null; }
        }

        public void InstallZapretService(string name) =>
            Busy($"Запускаю {name} — ставлю службу zapret", () => Zapret.InstallZapretServiceCore(name));

        void RemoveZapretService() => Busy("Удаляю службу zapret", Zapret.RemoveZapretServiceCore);

        void RemoveAllServices() => Busy("Останавливаю обход: служба zapret, winws и WinDivert", Zapret.RemoveAllServicesCore);

        public void StartStrategyManual(string name) => Busy($"Запускаю {name}", () => Zapret.StartStrategyManualCore(name));

        public void RestoreServiceAfterTest() => Busy("Возвращаю службу zapret после теста", Zapret.RestoreServiceAfterTestCore);

        // После теста вернуть ручную стратегию, которая работала до него (службу возвращает UpdateStatus)
        void RestorePreTestStrategy()
        {
            string name = _preTestManual;
            _preTestManual = null;
            if (name == null || Ctx.UiState.PendingServiceRestore != null) return;
            try { StartStrategyManual(name); }
            catch (Exception ex) { MessageDialog.ShowError($"Не удалось снова запустить {name} после теста: {ex.Message}"); }
        }

        // ---------- update flow ----------

        void CheckUpdateTick()
        {
            if (!Updater.UpdateCheckCompleted()) return;
            string remote = Ctx.NewVersion;
            BtnUpdate.Content = $"Доступна версия {remote}";
            BtnUpdate.Visibility = Visibility.Visible;
            if (Ctx.UiState.SkippedVersion != remote) ShowUpdateFlow(Zapret.GetLocalVersion(), remote);
        }

        void ShowUpdateFlow(string local, string remote)
        {
            if (UpdateDialog.Show(local, remote) != UpdateAction.Update) return;
            StartAutoUpdate(remote);
        }

        void StartAutoUpdate(string version)
        {
            if (Ctx.Run != null && !Ctx.Run.Finished) throw new Exception("Сейчас идёт тест. Дождитесь его окончания и обновитесь снова.");
            string target = UpdateProgressDialog.Show(version);
            if (target == null) return;
            // Новое окно само перенесёт настройки и переустановит службу — у него пути новой папки
            Process.Start(new ProcessStartInfo(Path.Combine(target, "zapret-ui.exe"), $"-MigrateFrom \"{Ctx.Root}\"")
            {
                WorkingDirectory = target,
                UseShellExecute = true,
            });
            Close();
        }

        // ---------- loaded / closing ----------

        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Safe.Run(() =>
            {
                try { Updater.StartUpdateCheck(); } catch { }
                // Как service.bat при запуске: заготовки пользовательских списков, если их нет
                try { Zapret.InitializeUserLists(); } catch { }
                // Остался от теста, прерванного вместе с окном
                try { if (!Zapret.GetState().Testing) Zapret.RemoveBaselineBat(); } catch { }
                try { Shortcuts.InitializeStartShortcut(); } catch { }
                try { Shortcuts.UpdateShortcutIcons(); } catch { }
                LoadSettings();
                LoadResultFiles();
                CbList.SelectedIndex = 0;
                UpdateStatus();
                ShowHistory();
                if (CbStrategy.SelectedItem == null)
                {
                    string last = Ctx.UiState.LastStrategy;
                    string best = Ctx.LatestResults?.Best;
                    if (last != null && CbStrategy.Items.Contains(last)) CbStrategy.SelectedItem = last;
                    else if (best != null && CbStrategy.Items.Contains(best)) CbStrategy.SelectedItem = best;
                    else if (CbStrategy.Items.Count > 0) CbStrategy.SelectedIndex = 0;
                }
                // Панель была открыта в прошлый раз — открываем с активной стратегией (или лучшей)
                string active = Ctx.State?.Active;
                var activeCfg = active != null && Ctx.Results != null
                    ? Ctx.Results.Configs.FirstOrDefault(c => c.Name == active)
                    : null;
                if (Ctx.UiState.DetailOpen && activeCfg != null && Ctx.SelectedResult != active) SelectResult(activeCfg);
                else RestoreDetailPanel();
                // Окно открыто старым после автообновления — переносим настройки и службу
                if (!string.IsNullOrEmpty(Ctx.MigrateFrom))
                {
                    try { CompleteAutoUpdate(Ctx.MigrateFrom); }
                    catch (Exception ex)
                    {
                        MessageDialog.ShowError($"Обновление прошло, но перенос настроек не завершился: {ex.Message}\n\nПеренесите их вручную: «Настройки» → «Перенести из старой версии».");
                    }
                }
                _timer.Start();
            });
        }

        void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try { ConfirmListChanges(); } catch { }
            if (Ctx.Run != null && !Ctx.Run.Finished)
            {
                var a = MessageDialog.Show("Тест ещё идёт. Прервать его и закрыть окно?", MessageKind.Question, true, "Прервать и закрыть", "Не закрывать");
                if (a != MessageResult.Yes) { e.Cancel = true; return; }
                try { StopTestRunCore(); } catch { }
                RestorePreTestStrategy();
                // Окно закрывается — вернуть службу прямо сейчас, а не при следующем запуске
                if (Ctx.UiState.PendingServiceRestore != null) { try { RestoreServiceAfterTest(); } catch { } }
            }
        }

        // Вызывается в новом окне после автообновления (-MigrateFrom)
        void CompleteAutoUpdate(string old)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(old, "service.bat"))) return;
            var state = Zapret.GetState();
            var items = Migration.GetMigrationItems(old);
            var ids = items.Where(i => i.On || i.Id == "ui-state").Select(i => i.Id).ToArray();
            var res = ids.Length > 0 ? Migration.InvokeMigration(old, ids) : new MigrationResult();
            UpdateAfterMigration();
            var lines = new List<string>(res.Done);

            string svcRoot = Zapret.GetServiceRoot();
            if (svcRoot != null && Zapret.TestSameFolder(svcRoot, old) && state.ServiceStrategy != null)
            {
                if (CbStrategy.Items.Contains(state.ServiceStrategy))
                {
                    InstallZapretService(state.ServiceStrategy);
                    lines.Add($"служба переустановлена из этой папки: {state.ServiceStrategy}");
                }
                else
                {
                    lines.Add($"служба осталась в старой папке: в новой версии нет стратегии {state.ServiceStrategy} — выберите другую и нажмите «Запустить»");
                }
            }
            else
            {
                // Ручная стратегия из старой папки: её командная строка здесь не узнаётся,
                // поэтому смотрим на путь к winws.exe, а имя берём из перенесённой истории
                bool oldWinwsRunning;
                try
                {
                    string prefix = old.TrimEnd('\\') + "\\";
                    oldWinwsRunning = Process.GetProcessesByName("winws")
                        .Any(p => { try { return p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                }
                catch { oldWinwsRunning = false; }
                string last = Ctx.UiState.LastStrategy;
                if (oldWinwsRunning && last != null && CbStrategy.Items.Contains(last))
                {
                    StartStrategyManual(last);
                    lines.Add($"стратегия {last} перезапущена из этой папки");
                }
            }
            try
            {
                var moved = Shortcuts.UpdateShortcutAfterUpdate(old);
                if (moved.Count > 0) lines.Add($"ярлык {string.Join(" и ", moved)} теперь открывает эту папку");
            }
            catch { }
            UpdateStatus();
            string msg = $"zapret обновлён до {Zapret.GetLocalVersion()}.\n\n" +
                (lines.Count > 0 ? "Сделано:\n  " + string.Join("\n  ", lines) : "Переносить было нечего.") +
                $"\n\nСтарая папка осталась на месте:\n{old}\nЕё можно удалить, когда убедитесь, что всё работает.";
            MessageDialog.Show(msg);
        }

        void UpdateAfterMigration()
        {
            Theme.Sync();
            LoadSettings();
            LoadList();
            LoadResultFiles();
            ShowHistory();
            UpdateStatus();
        }
    }
}
