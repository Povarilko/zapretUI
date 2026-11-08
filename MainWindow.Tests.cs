using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace ZapretUi
{
    public partial class MainWindow
    {
        DispatcherTimer _testTimer;

        void WireTests()
        {
            _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _testTimer.Tick += (s, e) =>
            {
                try { UpdateTestRun(); }
                catch (Exception ex)
                {
                    MessageDialog.ShowError("Ошибка при чтении хода теста: " + ex.Message);
                    _testTimer.Stop();
                }
            };

            BtnRunTests.Click += (s, e) => Safe.Run(() => { StartTests(); UpdateStatus(); });
            BtnTargets.Click += (s, e) => Safe.Run(() =>
            {
                if (!TargetsEditorDialog.ShowEditor()) return;
                // Колонки таблицы строятся по группам из targets.txt — перечитываем открытые результаты
                if (Ctx.Run != null && !Ctx.Run.Finished) return;
                if (Ctx.ResultFiles.Length > 0) Ctx.LatestResults = ResultsModel.ReadTestResults(Ctx.ResultFiles[0].FullName);
                int i = CbResults.SelectedIndex;
                if (i >= 0)
                {
                    Ctx.Results = ResultsModel.ReadTestResults(Ctx.ResultFiles[i].FullName);
                    ShowResults();
                }
            });
            BtnCancelTest.Click += (s, e) => Safe.Run(() =>
            {
                var a = MessageDialog.Show("Прервать тест? Уже проверенные стратегии останутся в таблице, но в файл результатов не попадут.", MessageKind.Question, true, "Прервать", "Продолжить");
                if (a == MessageResult.Yes) StopTestRunCore();
            });
            BtnHideTest.Click += (s, e) =>
            {
                TestRunner.DisposeRun(Ctx.Run);
                Ctx.Run = null;
                TbTestLog.Visibility = Visibility.Collapsed;
                UpdateTestPanel();
            };
            BtnRunBest.Click += (s, e) => Safe.Run(() =>
            {
                string best = Ctx.Results?.Best;
                if (best == null) return;
                CbStrategy.SelectedItem = best;
                StartStrategy(best);
            });
            BtnToggleLog.Click += (s, e) =>
            {
                TbTestLog.Visibility = TbTestLog.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (Ctx.Run != null) Ctx.Run.LogChanged = true;
                UpdateTestPanel();
            };
        }

        void StartTests()
        {
            if (Ctx.Run != null && !Ctx.Run.Finished) throw new Exception("Тест уже идёт.");
            var st = Zapret.GetState();
            if (st.Testing) throw new Exception("Тест уже запущен в другом окне.");
            var setup = TestSetupDialog.Ask();
            if (setup == null) return;
            if (Zapret.ServiceInstalled())
            {
                if (!Ctx.UiState.AutoRemoveServiceForTest)
                {
                    var answer = ConfirmServiceDialog.ShowForTest(this, st.ServiceStrategy);
                    if (!answer.Ok) return;
                    if (answer.DontAsk)
                    {
                        Ctx.UiState.AutoRemoveServiceForTest = true;
                        Ctx.UiState.Save();
                        LoadSettings();
                    }
                }
                Busy("Удаляю службу zapret", Zapret.RemoveServiceForTest);
            }
            // Ручную стратегию останавливаем сами и после теста запускаем снова без консоли
            _preTestManual = st.Mode == "manual" ? st.Active : null;
            if (_preTestManual != null) Proc.KillByName("winws");

            // «Без обхода» — служебный .bat на время теста
            if (setup.Baseline) Zapret.NewBaselineBat();
            else Zapret.RemoveBaselineBat();
            var names = setup.Names.Concat(setup.Baseline ? new[] { Ctx.BaselineName } : Array.Empty<string>()).ToList();

            // Ответы на вопросы скрипта после типа теста: все или выбранные стратегии, номера стратегий
            var tail = new List<string>();
            if (setup.All) tail.Add("1");
            else
            {
                tail.Add("2");
                var all = TestRunner.AllBatFilesIncludingBaseline();
                var idx = new List<int>();
                for (int i = 0; i < all.Length; i++)
                    if (names.Contains(all[i].Name)) idx.Add(i + 1);
                tail.Add(string.Join(",", idx));
            }

            Ctx.TestStartedAt = DateTime.Now;
            Ctx.Run = new TestRun
            {
                Kind = setup.Type,
                Phases = TestRunner.GetTestPhases(setup.Type),
                Phase = 0,
                AnswerTail = tail,
                Groups = Targets.GetTargetGroups(),
                Total = names.Count,
                Started = DateTime.Now,
                PhaseStarted = DateTime.Now,
            };
            TestRunner.StartTestPhase(Ctx.Run);
            Ctx.SelectedResult = null;
            ClearDetail();
            Ctx.Results = ResultsModel.NewResultSet(Array.Empty<Config>(), null, setup.Type);
            ShowResults();
            CbResults.IsEnabled = false;
            TbTestLog.Text = "";
            UpdateTestPanel();
            UpdateStartButton();
            _testTimer.Start();
        }

        void StopTestRunCore()
        {
            TestRunner.StopTestRun();
            UpdateTestRun();
        }

        // Тик таймера, пока идёт тест
        void UpdateTestRun()
        {
            var r = Ctx.Run;
            if (r == null || r.Finished) return;
            TestRunner.ReadTestLog();
            bool exited = r.Proc == null || r.Proc.HasExited;
            if (exited)
            {
                TestRunner.ReadTestLog();
                if (r.Partial.Length > 0) { TestRunner.AddTestLine(r.Partial); r.Partial = ""; }
                // Прерванную на середине стратегию в таблицу не кладём
                if (r.Cancelled) r.Current = null;
                else TestRunner.CompleteTestConfig();
                if (!r.Cancelled && r.ResultFile != null)
                {
                    TestRunner.UpdateTestPace(r.Type, r.Durations);
                    // Файл этапа — по времени записи: путь из вывода теста может прийти с искажённой кириллицей
                    var file = ResultsModel.GetResultFiles()
                        .Where(f => !f.Name.Contains("_combined") && f.LastWriteTime >= r.PhaseStarted.AddSeconds(-2))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    if (file != null) r.ResultFiles.Add(file.FullName);
                    // Комбинированный тест: после обычного — DPI с теми же стратегиями
                    if (r.Phase < r.Phases.Count - 1)
                    {
                        r.PrevDone.AddRange(r.Done);
                        r.Phase++;
                        r.Log.AppendLine("");
                        r.Log.AppendLine("==================== " + ResultsModel.TestTypeName(r.Phases[r.Phase]) + " ====================");
                        TestRunner.StartTestPhase(r);
                        ShowLiveResults();
                        UpdateTestPanel();
                        return;
                    }
                    if (r.ResultFiles.Count > 1) r.ResultFile = ResultsModel.JoinTestResultFiles(r.ResultFiles.Select(f => new FileInfo(f)).ToList());
                }
                r.Finished = true;
                Zapret.RemoveBaselineBat();
                _testTimer.Stop();
                RestorePreTestStrategy();
                CbResults.IsEnabled = true;
                if (r.ResultFile != null && !r.Cancelled) LoadResultFiles();
                else if (r.Done.Count > 0 || r.PrevDone.Count > 0) ShowLiveResults();
                else LoadResultFiles();
                UpdateStatus();
            }
            else if (r.Changed)
            {
                r.Changed = false;
                ShowLiveResults();
            }
            else if (r.Current != null && r.Index > 0 && !r.ShownCurrent)
            {
                ShowLiveResults();
            }
            r.ShownCurrent = r.Current != null;
            UpdateTestPanel();
        }

        void ShowLiveResults()
        {
            var r = Ctx.Run;
            var done = r.Done;
            if (r.PrevDone.Count > 0)
            {
                // Второй этап комбинированного теста: к строкам обычного теста добавляется колонка DPI
                var all = r.PrevDone.Concat(done).Concat(r.Current != null ? new[] { r.Current } : Array.Empty<Config>()).ToList();
                string bestStandard = ResultsModel.NewResultSet(r.PrevDone.ToList(), null, "standard").Best;
                Ctx.Results = ResultsModel.NewResultSet(ResultsModel.MergeTestConfigs(all), bestStandard, r.Kind);
            }
            else
            {
                var set = ResultsModel.NewResultSet(done, null, r.Kind);
                if (r.Current != null) set.Configs = set.Configs.Concat(new[] { r.Current }).ToList();
                Ctx.Results = set;
            }
            ShowResults();
        }

        void UpdateTestPanel()
        {
            var r = Ctx.Run;
            if (r == null) { TestPanel.Visibility = Visibility.Collapsed; return; }
            TestPanel.Visibility = Visibility.Visible;
            string elapsed = TestRunner.FormatElapsed(DateTime.Now - r.Started);
            int total = Math.Max(1, r.Total);
            // Полоса — по всем этапам: у комбинированного теста каждая стратегия проверяется дважды
            PbTest.Maximum = total * r.Phases.Count;
            string phaseNote = r.Phases.Count > 1
                ? $"{ResultsModel.TestTypeName(r.Type)}, этап {r.Phase + 1} из {r.Phases.Count} · "
                : $"{ResultsModel.TestTypeName(r.Type)} · ";

            if (!r.Finished)
            {
                PbTest.Value = Math.Max(0, r.Phase * total + r.Done.Count);
                string timeNote = $"прошло {elapsed} · осталось {TestRunner.FormatEstimate(TestRunner.GetTestRemaining(r))}";
                if (r.Index > 0)
                {
                    TxtTestTitle.Text = $"Идёт тест · {phaseNote}{r.Done.Count} из {r.Total}";
                    TxtTestSub.Text = (r.Current != null ? $"Сейчас проверяется {GetDisplayName(r.Current.Name)}" : "Подводятся итоги") + $" · {timeNote}";
                }
                else
                {
                    TxtTestTitle.Text = r.Phase > 0 ? $"Идёт тест · {phaseNote}запуск" : $"Тест запускается · {phaseNote}".TrimEnd(' ', '·');
                    TxtTestSub.Text = (r.Type == "dpi" ? "Скрипт загружает список целей DPI" : "Скрипт проверяет права и curl") + $" · {timeNote}";
                }
                BtnCancelTest.Visibility = Visibility.Visible;
                BtnHideTest.Visibility = Visibility.Collapsed;
                BtnRunBest.Visibility = Visibility.Collapsed;
            }
            else
            {
                PbTest.Value = r.Cancelled ? r.Phase * total + r.Done.Count : total * r.Phases.Count;
                string best = Ctx.Results?.Best;
                if (r.Cancelled)
                {
                    TxtTestTitle.Text = $"Тест прерван · {phaseNote}" + $"проверено {r.Done.Count} из {r.Total}";
                    TxtTestSub.Text = r.ResultFiles.Count > 0
                        ? "Обход возвращён в состояние до теста. Обычный тест сохранён в результатах, DPI — только в таблице."
                        : "Обход возвращён в состояние до теста. Частичные результаты видны в таблице, в файл они не сохранены.";
                }
                else if (r.ResultFile != null)
                {
                    TxtTestTitle.Text = $"Тест завершён за {elapsed} · {ResultsModel.TestTypeName(r.Kind)} · проверено {r.Total}";
                    TxtTestSub.Text = best != null ? $"Лучшая: {best}" : "";
                }
                else
                {
                    TxtTestTitle.Text = "Тест не запустился";
                    TxtTestSub.Text = r.Errors.Count > 0 ? string.Join(" · ", r.Errors) : "Подробности в журнале.";
                }
                BtnCancelTest.Visibility = Visibility.Collapsed;
                BtnHideTest.Visibility = Visibility.Visible;
                BtnRunBest.Visibility = best != null && r.ResultFile != null && CbStrategy.Items.Contains(best) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TbTestLog.Visibility == Visibility.Visible && r.LogChanged)
            {
                TbTestLog.Text = r.Log.ToString();
                TbTestLog.ScrollToEnd();
                r.LogChanged = false;
            }
        }
    }
}
