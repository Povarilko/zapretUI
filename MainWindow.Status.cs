using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZapretUi
{
    public partial class MainWindow
    {
        // ---------- events ----------

        void WireStatus()
        {
            // Подсказка видна и у заблокированной кнопки — чтобы было понятно, почему она серая
            ToolTipService.SetShowOnDisabled(BtnStart, true);
            ToolTipService.SetShowOnDisabled(CbStrategy, true);

            BtnStart.Click += (s, e) => Safe.Run(() =>
            {
                if (Ctx.StartIsStop)
                {
                    // «Остановить» = Remove Services: служба, winws.exe и драйвер WinDivert
                    Cursor = Cursors.Wait;
                    try { RemoveAllServices(); }
                    finally { Cursor = null; }
                }
                else
                {
                    if (CbStrategy.SelectedItem == null) throw new Exception("Выберите стратегию в списке.");
                    StartStrategy((string)CbStrategy.SelectedItem);
                }
                UpdateStatus();
            });
            CbStrategy.SelectionChanged += (s, e) => UpdateStartButton();
            BtnApplySettings.Click += (s, e) => Safe.Run(() =>
            {
                Cursor = Cursors.Wait;
                try { RestartActive(); }
                finally { Cursor = null; }
                TxtSettingsMsg.Text = "";
                SettingsMsgPanel.Visibility = Visibility.Collapsed;
                UpdateStatus();
            });
            BtnStartSelected.Click += (s, e) => Safe.Run(() =>
            {
                if (Ctx.SelectedResult != null)
                {
                    CbStrategy.SelectedItem = Ctx.SelectedResult;
                    StartStrategy(Ctx.SelectedResult);
                }
            });
            BtnCloseDetail.Click += (s, e) => CloseDetail();
        }

        // ---------- status ----------

        public void UpdateStatus()
        {
            var prev = Ctx.State;
            var st = Zapret.GetState();

            // Тест закончился — возвращаем службу, убранную перед тестом.
            // Первые секунды после запуска пропускаем: процесс теста может ещё не появиться.
            if (Ctx.UiState.PendingServiceRestore != null && !Ctx.AutoTuneActive && !AutoTune.TuneLockActive && !st.Testing
                && !(Ctx.Run != null && !Ctx.Run.Finished)
                && DateTime.Now > Ctx.TestStartedAt.AddSeconds(10))
            {
                try { RestoreServiceAfterTest(); }
                catch (Exception ex)
                {
                    // Сначала сбрасываем, иначе таймер повторит попытку, пока открыто сообщение
                    Ctx.UiState.PendingServiceRestore = null;
                    Ctx.UiState.Save();
                    MessageDialog.ShowError($"Не удалось вернуть службу zapret после теста: {ex.Message}\n\nВыберите стратегию вверху окна и нажмите «Запустить» — служба установится заново.");
                }
                st = Zapret.GetState();
            }
            Ctx.State = st;
            BtnRunTests.IsEnabled = !st.Testing;
            ServiceNote.Visibility = Zapret.ServiceInstalled() ? Visibility.Collapsed : Visibility.Visible;

            switch (st.ServiceStatus)
            {
                case null: Ui.Pill(PillService, PillServiceText, "Служба не установлена", "na"); break;
                case "Running": Ui.Pill(PillService, PillServiceText, "Служба запущена", "good"); break;
                case "StartPending": Ui.Pill(PillService, PillServiceText, "Служба запускается", "mid"); break;
                case "StopPending": Ui.Pill(PillService, PillServiceText, "Служба останавливается", "mid"); break;
                // Окно так не делает, но её могли остановить вручную
                default: Ui.Pill(PillService, PillServiceText, "Служба остановлена", "bad"); break;
            }
            PillService.ToolTip = !st.ServiceExists
                ? "Служба zapret не установлена: после перезагрузки обход сам не запустится"
                : st.ServiceStrategy != null
                    ? $"Служба zapret со стратегией {st.ServiceStrategy}, запускается вместе с Windows"
                    : "Служба zapret, запускается вместе с Windows";
            // TG WS Proxy: плашка в шапке, а открытая вкладка — вместе со статусом
            try
            {
                UpdateTgPill();
                if (Tabs.SelectedItem == TabTg && TgProxy.Download == null) UpdateTgTab();
            }
            catch { }

            if (st.Running) Ui.Pill(PillWinws, PillWinwsText, "winws.exe работает", "good");
            else Ui.Pill(PillWinws, PillWinwsText, "winws.exe не запущен", "bad");
            PillWinws.ToolTip = st.Running ? "Процесс обхода запущен" : "Обход сейчас не работает";

            if (st.DivertStatus == null) Ui.Pill(PillDivert, PillDivertText, "WinDivert не загружен", "na");
            else if (st.DivertStatus == "Running") Ui.Pill(PillDivert, PillDivertText, "WinDivert активен", "good");
            else Ui.Pill(PillDivert, PillDivertText, $"WinDivert: {st.DivertStatus}", "mid");
            PillDivert.ToolTip = "Драйвер перехвата трафика, его загружает winws.exe";

            UpdateStartButton();

            if (UpdateHistory(st)) ShowHistory();
            string lastName = Ctx.UiState.LastStrategy;

            string score = null;
            TxtActive.Foreground = Theme.Pal["Text"];
            TxtActiveLabel.Text = "Активная стратегия";
            string sub;
            if (st.Testing)
            {
                TxtActiveLabel.Text = "Идёт тест стратегий";
                string checking = Ctx.Run?.Current != null ? Ctx.Run.Current.Name : st.Active;
                TxtActive.Text = checking != null ? $"проверяется {checking}" : "тест запущен";
                sub = "история не записывается";
                if (Ctx.UiState.PendingServiceRestore != null) sub += " · служба вернётся после теста";
                else if (lastName != null) sub += $" · последняя: {lastName}";
            }
            else
            {
                switch (st.Mode)
                {
                    case "service":
                        TxtActive.Text = st.Active ?? "служба zapret";
                        sub = "служба zapret, запускается вместе с Windows";
                        break;
                    case "manual":
                        TxtActive.Text = st.Active;
                        sub = "запущена вручную";
                        break;
                    case "unknown":
                        TxtActive.Text = "winws.exe работает";
                        sub = "не удалось понять, какая стратегия запущена";
                        if (lastName != null) sub += $" · последняя: {lastName}";
                        break;
                    default:
                        if (lastName != null)
                        {
                            TxtActiveLabel.Text = "Последняя стратегия";
                            TxtActive.Text = lastName;
                            TxtActive.Foreground = Theme.Pal["Muted"];
                            sub = "сейчас не запущена, обход выключен";
                        }
                        else
                        {
                            TxtActive.Text = "Ничего не запущено";
                            sub = "обход выключен";
                        }
                        break;
                }
                string scoreName = st.Active ?? (st.Mode == "none" ? lastName : null);
                if (scoreName != null) score = ResultsModel.GetResultScore(scoreName);
            }
            TxtActiveSub.Text = score != null ? $"{sub} · {score}" : sub;

            // Во время теста выпадающий список не прыгает за перебираемыми стратегиями
            if (!st.Testing)
            {
                string shown = st.Active ?? lastName;
                string prevShown = prev != null && !prev.Testing ? (prev.Active ?? Ctx.PrevLastName) : null;
                if (shown != null && shown != prevShown && CbStrategy.Items.Contains(shown)) CbStrategy.SelectedItem = shown;
                if (Ctx.Results != null && (prev == null || prev.Active != st.Active || prev.Testing)) ShowResults();
            }
            Ctx.PrevLastName = lastName;

            var files = ResultsModel.GetResultFiles();
            if (files.Length != Ctx.ResultFiles.Length) LoadResultFiles();
        }

        // «Запустить» ⇄ «Остановить»: пока обход работает, список стратегий заблокирован
        // на работающей стратегии, а кнопка останавливает
        void UpdateStartButton()
        {
            var st = Ctx.State;
            bool busy = (st != null && st.Testing)
                || (Ctx.Run != null && !Ctx.Run.Finished)
                || Ctx.UiState.PendingServiceRestore != null;
            if (busy)
            {
                CbStrategy.IsEnabled = false;
                CbStrategy.ToolTip = "Идёт тест. Выбор стратегии станет доступен, когда он закончится.";
                BtnStart.IsEnabled = false;
                BtnStart.ToolTip = "Идёт тест. Кнопка станет доступна, когда он закончится.";
                // Окно открыли, когда тест уже шёл (в другом окне), — у кнопки ещё нет надписи
                if (BtnStart.Content == null)
                {
                    BtnStart.Style = (Style)FindResource("Primary");
                    BtnStart.Tag = "\uE768";
                    BtnStart.Content = "Запустить";
                }
                return;
            }
            bool running = st != null && !st.Testing && st.Active != null
                && CbStrategy.Items.Contains(st.Active)
                && ((st.Mode == "service" && st.ServiceStatus == "Running") || st.Mode == "manual");
            if (running)
            {
                if ((string)CbStrategy.SelectedItem != st.Active) CbStrategy.SelectedItem = st.Active;
                CbStrategy.IsEnabled = false;
                CbStrategy.ToolTip = "Стратегия сейчас работает. Чтобы выбрать другую, нажмите «Остановить».";
            }
            else
            {
                CbStrategy.IsEnabled = true;
                CbStrategy.ToolTip = null;
            }
            if (running != Ctx.StartIsStop || BtnStart.Content == null)
            {
                Ctx.StartIsStop = running;
                BtnStart.Style = (Style)FindResource("Primary");
                if (running)
                {
                    BtnStart.Tag = "\uE71A";
                    BtnStart.Content = "Остановить";
                    Ctx.StartTip = "Удаляет службу и останавливает обход (Remove Services в service.bat)";
                }
                else
                {
                    BtnStart.Tag = "\uE768";
                    BtnStart.Content = "Запустить";
                    Ctx.StartTip = "Ставит выбранную стратегию в службу zapret";
                }
            }
            BtnStart.IsEnabled = true;
            BtnStart.ToolTip = Ctx.StartTip;
        }

        // Перезапуск той же стратегии («Применить сейчас» в настройках)
        void RestartActive()
        {
            var st = Zapret.GetState();
            if (st.ServiceStrategy != null) { InstallZapretService(st.ServiceStrategy); return; }
            if (st.Mode == "manual") { StartStrategyManual(st.Active); return; }
            throw new Exception("Сейчас нечего перезапускать.");
        }

        // Вызывается на каждом опросе статуса: пишет запуск или остановку, если что-то поменялось
        bool UpdateHistory(ZapretState st)
        {
            if (st.Testing) return false;
            if (st.Mode == "service" || st.Mode == "manual")
            {
                if (st.Active == null) return false;
                string source = st.Mode == "service" ? "service" : (Ctx.PendingSource ?? "manual");
                bool changed = Ctx.UiState.AddHistoryEvent("start", st.Active, source);
                Ctx.PendingSource = null;
                Ctx.UiState.SetLastStrategy(st.Active);
                return changed;
            }
            // Пока стратегия перезапускается, winws.exe ненадолго пропадает: это не остановка
            if (st.Mode == "none" && DateTime.Now > Ctx.GraceUntil)
                return Ctx.UiState.AddHistoryEvent("stop", null, null);
            return false;
        }
    }
}
