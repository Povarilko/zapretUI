using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ZapretUi
{
    // Мастер автоподбора: предтест стоковых стратегий → спуск по осям → валидация.
    // Прогоны — штатным test zapret.ps1, кандидаты — временные ~auto-*.bat в папке zapret.
    public partial class AutoTuneDialog : Window
    {
        enum PhaseKind { PreTest, Descent, Validate, Done }

        readonly DispatcherTimer _timer;
        RedirectedProcess _proc;
        DateTime _phaseStarted;
        long _logPos;
        PhaseKind _phase;
        int _axisIdx;       // текущая ось в фазе спуска
        int _pass;          // проход спуска (1..2)
        string _template;   // имя .bat-шаблона (лучшая стоковая)
        string _championArgs, _controlArgs;
        Config _champion, _control;
        string _championName = AutoTune.OutputName;
        readonly List<(string Name, string Args)> _roundCandidates = new List<(string, string)>();
        int _roundsDone, _roundsTotal;

        public AutoTuneDialog()
        {
            InitializeComponent();
            DialogBase.Init(this);
            if (Ctx.Main != null) Owner = Ctx.Main;

            var strategies = Zapret.GetStrategies()
                .Where(s => !s.Name.StartsWith("~"))
                .ToList();
            foreach (var s in strategies)
            {
                var cb = new CheckBox { Content = s.Name, IsChecked = s.Name != "general (BEST)", Margin = new Thickness(2, 2, 0, 2) };
                cb.Tag = s.Name;
                StrategyList.Children.Add(cb);
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += (s, e) => Tick();

            BtnStart.Click += (s, e) => Safe.Run(Start);
            BtnSave.Click += (s, e) => Safe.Run(SaveResult);
            BtnClose.Click += (s, e) => { if (ConfirmStop()) Close(); };
            Closed += (s, e) => { Ctx.AutoTuneActive = false; StopProcess(); AutoTune.CleanCandidates(); };
        }

        bool ConfirmStop()
        {
            if (_phase == PhaseKind.Done || _proc == null) return true;
            return MessageDialog.Show("Подбор ещё идёт. Прервать его и закрыть окно?", MessageKind.Question, true, "Прервать", "Продолжить") == MessageResult.Yes;
        }

        void StopProcess()
        {
            _timer.Stop();
            if (_proc != null)
            {
                try { _proc.KillTree(); } catch { }
                try { _proc.Dispose(); } catch { }
                _proc = null;
            }
            Proc.KillByName("winws");
        }

        // ---------- план ----------

        void Start()
        {
            var selected = StrategyList.Children.Cast<CheckBox>()
                .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (selected.Count < 2)
                throw new Exception("Отметьте хотя бы две стратегии для предтеста.");

            // Тот же пролог, что у обычного теста: test zapret.ps1 не работает при установленной службе
            var st = Zapret.GetState();
            if (st.Testing) throw new Exception("Тест уже запущен в другом окне.");
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
                    }
                }
                if (Ctx.Main != null) Ctx.Main.Busy("Удаляю службу zapret", Zapret.RemoveServiceForTest);
                else Zapret.RemoveServiceForTest();
            }
            if (st.Mode == "manual") Proc.KillByName("winws");

            BtnStart.IsEnabled = false;
            StrategiesCard.IsEnabled = false;
            AutoTune.CleanCandidates();
            Ctx.AutoTuneActive = true; // главное окно не возвращает службу посреди подбора

            _phase = PhaseKind.PreTest;
            _phaseStarted = DateTime.Now;
            _roundsTotal = 1 + AutoTune.Axes.Length * 2 + 1;
            _roundsDone = 0;

            var answers = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(selected)) };
            BeginRun(answers, "Предтест: проверяю " + selected.Count + " стратегий…");
        }

        void BeginRun(List<string> answers, string phaseText)
        {
            PhaseText.Text = phaseText;
            Progress.Value = 100.0 * _roundsDone / Math.Max(1, _roundsTotal);
            AppendLog("=== " + phaseText);
            // Диагностика рассинхрона номеров: файлы кандидатов на месте, номера какие уйдут скрипту
            foreach (var c in _roundCandidates)
            {
                string p = Path.Combine(Ctx.Root, AutoTune.CandidatePrefix + c.Name + ".bat");
                if (!File.Exists(p)) AppendLog("!! нет файла " + Path.GetFileName(p));
            }
            AppendLog(">> ответы скрипту: " + string.Join(" / ", answers));
            int ourCount = Directory.GetFiles(Ctx.Root, "*.bat")
                .Count(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase));
            AppendLog(">> .bat в папке по версии приложения: " + ourCount);
            File.WriteAllText(Ctx.TestInput, string.Join("\r\n", answers) + "\r\n");
            try { File.Delete(Ctx.TestLog); } catch { }
            try { File.Delete(Ctx.TestLog + ".err"); } catch { }
            _logPos = 0;
            _phaseStarted = DateTime.Now;
            _proc = RedirectedProcess.Start("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Ctx.TestPs}\"", Ctx.UtilsDir,
                Ctx.TestInput, Ctx.TestLog, Ctx.TestLog + ".err");
            _timer.Start();
        }

        void Tick()
        {
            ReadLog();
            if (_proc == null || !_proc.HasExited) return;
            var p = _proc;
            _proc = null;
            try { p.Dispose(); } catch { }
            try
            {
                CheckConfigCount();
                CompletePhase();
            }
            catch (Exception ex)
            {
                // Пользователю — коротко; технические детали — в журнал мастера
                ReadLog();
                var tail = Log.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(l => l.Trim().Length > 0).ToList();
                AppendLog("ОШИБКА ЭТАПА: " + ex);
                AppendLog("Хвост журнала теста: " + string.Join(" | ", tail.Skip(Math.Max(0, tail.Count - 6))));
                MessageDialog.Show(ex.Message, MessageKind.Warning);
                PhaseDone("Подбор прерван.");
            }
        }

        // Скрипт перед выбором печатает пронумерованный список доступных конфигов.
        // Сверяем его с нашим порядком целиком: любое расхождение означает, что номера
        // выбранных стратегий сместились — лучше остановить этап, чем тестировать чужих
        void CheckConfigCount()
        {
            string text;
            try
            {
                if (!File.Exists(Ctx.TestLog)) return;
                text = Oem.Decode(File.ReadAllBytes(Ctx.TestLog));
            }
            catch { return; }
            var scriptList = Regex.Matches(text, @"\[\d+\]\s+(.+?\.bat)\s*$", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();
            if (scriptList.Count == 0) return;
            var ourList = Directory.GetFiles(Ctx.Root, "*.bat")
                .Select(Path.GetFileName)
                .Where(f => !f.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, Str.NaturalComparer)
                .ToList();
            if (scriptList.Count != ourList.Count
                || scriptList.Where((t, i) => !string.Equals(t, ourList[i], StringComparison.OrdinalIgnoreCase)).Any())
            {
                string firstDiff = "";
                for (int i = 0; i < Math.Min(scriptList.Count, ourList.Count); i++)
                    if (!string.Equals(scriptList[i], ourList[i], StringComparison.OrdinalIgnoreCase))
                    {
                        firstDiff = $"\nПервое расхождение на позиции {i + 1}: скрипт видит «{scriptList[i]}», окно — «{ourList[i]}».";
                        break;
                    }
                throw new Exception(
                    $"Список конфигураций скрипта теста ({scriptList.Count}) не совпал со списком окна ({ourList.Count}) — номера стратегий сместились, этап остановлен.{firstDiff}\nПопробуйте начать подбор заново.");
            }
        }

        void ReadLog()
        {
            try
            {
                if (!File.Exists(Ctx.TestLog)) return;
                using var fs = new FileStream(Ctx.TestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= _logPos) return;
                fs.Position = _logPos;
                var buf = new byte[fs.Length - _logPos];
                int n = fs.Read(buf, 0, buf.Length);
                _logPos += n;
                var text = Oem.Decode(buf, 0, n);
                var tail = Log.Text + text;
                if (tail.Length > 20000) tail = tail.Substring(tail.Length - 20000);
                Log.Text = tail;
                Log.ScrollToEnd();
            }
            catch { }
        }

        void AppendLog(string line)
        {
            Log.Text = (Log.Text + "\r\n" + line).TrimStart('\r', '\n');
            Log.ScrollToEnd();
            try { File.AppendAllText(Path.Combine(Ctx.UtilsDir, "autotune.log"), line + "\r\n"); } catch { }
        }

        // ---------- результаты ----------

        List<Config> ReadLatestResults()
        {
            var files = ResultsModel.GetResultFiles()
                .Where(f => f.LastWriteTime >= _phaseStarted.AddSeconds(-5))
                .ToList();
            if (!files.Any()) throw new Exception("Тест не сохранил файл результатов.");
            return ResultsModel.ReadTestResults(files.OrderByDescending(f => f.LastWriteTime).First().FullName).Configs;
        }

        static int Compare(Config a, Config b, string argsA, string argsB)
        {
            double ra = a.Total > 0 ? (double)a.Ok / a.Total : 0;
            double rb = b.Total > 0 ? (double)b.Ok / b.Total : 0;
            if (Math.Abs(ra - rb) > 0.001) return ra > rb ? -1 : 1;
            if (a.PingFail != b.PingFail) return a.PingFail < b.PingFail ? -1 : 1;
            return AutoTune.CostOf(argsA).CompareTo(AutoTune.CostOf(argsB));
        }

        void CompletePhase()
        {
            ReadLog();
            var results = ReadLatestResults();
            _roundsDone++;

            if (_phase == PhaseKind.PreTest)
            {
                var best = results
                    .Where(c => !c.Name.StartsWith(AutoTune.CandidatePrefix) && c.Name != Ctx.BaselineName)
                    .OrderByDescending(c => c.Total > 0 ? (double)c.Ok / c.Total : 0)
                    .ThenBy(c => c.PingFail)
                    .FirstOrDefault();
                if (best == null) throw new Exception("Предтест не дал результатов.");
                _template = best.Name;
                _championArgs = Zapret.GetBatArgs(Path.Combine(Ctx.Root, best.Name + ".bat"));
                _champion = best;
                _controlArgs = _championArgs;
                _control = best;
                AppendLog($"Лучшая стоковая: {best.Name} ({best.Ok}/{best.Total}). Спуск от неё.");
                _phase = PhaseKind.Descent;
                _pass = 1; _axisIdx = -1;
                NextAxisOrFinish();
                return;
            }

            if (_phase == PhaseKind.Descent)
            {
                var roundBest = _roundCandidates
                    .Select(c => (cfg: results.FirstOrDefault(r => r.Name == AutoTune.CandidatePrefix + c.Name), args: c.Args))
                    .Where(x => x.cfg != null)
                    .ToList();
                roundBest.Sort((x, y) => Compare(x.cfg, y.cfg, x.args, y.args));
                var winner = roundBest.FirstOrDefault();
                bool improved = winner.cfg != null && Compare(winner.cfg, _champion, winner.args, _championArgs) < 0;
                if (improved)
                {
                    _champion = winner.cfg;
                    _championArgs = winner.args;
                    AppendLog($"→ улучшение: {winner.cfg.Ok}/{winner.cfg.Total} · {AutoTune.Describe(winner.args)}");
                }
                else
                {
                    AppendLog("→ без улучшения, остаёмся на текущем.");
                }
                NextAxisOrFinish();
                return;
            }

            if (_phase == PhaseKind.Validate)
            {
                var champRuns = _roundCandidates
                    .Select(c => results.FirstOrDefault(r => r.Name == AutoTune.CandidatePrefix + c.Name))
                    .Where(c => c != null).ToList();
                var control = results.FirstOrDefault(r => r.Name == _control.Name);
                if (!champRuns.Any()) throw new Exception("Валидация не дала результатов.");
                Config worst = champRuns.OrderBy(c => c.Total > 0 ? (double)c.Ok / c.Total : 0).First();
                bool ok = control == null || Compare(worst, control, _championArgs, _controlArgs) <= 0;
                if (!ok)
                {
                    _championArgs = _controlArgs;
                    _champion = _control;
                    _championName = _control.Name + " (стоковая)";
                    AppendLog("Подобранный вариант не подтвердился — оставляем стоковую.");
                }
                else
                {
                    AppendLog($"Валидация пройдена: худший прогон {worst.Ok}/{worst.Total}.");
                }
                PhaseDone(null);
            }
        }

        void NextAxisOrFinish()
        {
            _axisIdx++;
            if (_axisIdx >= AutoTune.Axes.Length)
            {
                _axisIdx = 0;
                _pass++;
            }
            if (_pass > 2)
            {
                _phase = PhaseKind.Validate;
                _roundCandidates.Clear();
                AutoTune.CleanCandidates();
                var names = new List<string>();
                for (int i = 1; i <= 2; i++)
                {
                    string n = $"final-{i}";
                    AutoTune.WriteCandidate(n, _championArgs);
                    _roundCandidates.Add((n, _championArgs));
                    names.Add(AutoTune.CandidatePrefix + n);
                }
                names.Add(_control.Name);
                var answers = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(names)) };
                BeginRun(answers, "Валидация: чемпион (два прогона) против стоковой…");
                return;
            }
            var (title, gen) = AutoTune.Axes[_axisIdx];
            var variants = gen(_championArgs)
                .Where(v => v != _championArgs)
                .GroupBy(v => AutoTune.Describe(v)).Select(g => g.First())
                .Take(5)
                .ToList();
            if (variants.Count == 0) { NextAxisOrFinish(); return; }
            AutoTune.CleanCandidates();
            _roundCandidates.Clear();
            var candNames = new List<string>();
            for (int i = 0; i < variants.Count; i++)
            {
                string n = $"a{_axisIdx}p{_pass}-{i}";
                AutoTune.WriteCandidate(n, variants[i]);
                _roundCandidates.Add((n, variants[i]));
                candNames.Add(AutoTune.CandidatePrefix + n);
            }
            var ans = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(candNames)) };
            BeginRun(ans, $"Спуск, проход {_pass}: ось «{title}» ({variants.Count} вариантов)");
        }

        void PhaseDone(string note)
        {
            _phase = PhaseKind.Done;
            Ctx.AutoTuneActive = false;
            _timer.Stop();
            Progress.Value = 100;
            StrategiesCard.IsEnabled = true;
            if (note != null) PhaseText.Text = note;
            else PhaseText.Text = "Готово: " + _championName;
            BtnSave.Visibility = Visibility.Visible;
            BtnStart.IsEnabled = true;
            BtnStart.Content = "Начать заново";
            AutoTune.CleanCandidates();
            AppendLog("");
            if (_champion != null)
            {
                AppendLog($"Итог: {_championName} — {_champion.Ok}/{_champion.Total}");
                AppendLog("Параметры: " + AutoTune.Describe(_championArgs));
            }
        }

        // ---------- сохранение ----------

        void SaveResult()
        {
            string templatePath = Path.Combine(Ctx.Root, _template + ".bat");
            string content = File.ReadAllText(templatePath);
            string oldArgs = Zapret.GetBatArgs(templatePath);
            string newContent = Regex.Replace(content,
                "(start \"zapret: %~n0\" /min \"%BIN%winws\\.exe\" ).*?\\r?\\n",
                m => m.Groups[1].Value + _championArgs + "\r\n",
                RegexOptions.Singleline);
            if (newContent == content)
                newContent = content.Replace(oldArgs, _championArgs);
            string outp = Path.Combine(Ctx.Root, AutoTune.OutputName + ".bat");
            File.WriteAllText(outp, newContent);
            MessageDialog.Show("Сохранено как «" + AutoTune.OutputName + "». Стратегия появится в списке вверху окна.", MessageKind.Info);
            DialogResult = true;
        }

        public static void Show()
        {
            var d = new AutoTuneDialog();
            d.ShowDialog();
            if (Ctx.Main == null) return;
            var current = Ctx.Main.CbStrategy.SelectedItem as string;
            Ctx.Strategies = Zapret.GetStrategies();
            Ctx.Main.CbStrategy.Items.Clear();
            foreach (var s in Ctx.Strategies) Ctx.Main.CbStrategy.Items.Add(s.Name);
            if (current != null && Ctx.Main.CbStrategy.Items.Contains(current)) Ctx.Main.CbStrategy.SelectedItem = current;
        }
    }
}
