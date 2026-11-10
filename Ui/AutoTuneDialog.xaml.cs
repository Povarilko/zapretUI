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
    // Мастер автоподбора: предтест стоковых стратегий → спуск от двух лучших
    // (каждый кандидат по два прогона, в зачёт худший) → валидация чемпиона
    // тремя прогонами. Прогоны — штатным test zapret.ps1, кандидаты — ~auto-*.bat.
    public partial class AutoTuneDialog : Window
    {
        enum PhaseKind { PreTest, Descent, Validate, FinalTest, Done }

        readonly DispatcherTimer _timer;
        RedirectedProcess _proc;
        DateTime _phaseStarted;
        long _logPos;
        PhaseKind _phase;
        int _axisIdx;       // текущая ось
        int _pass;          // проход спуска (1..2)
        int _startIdx;      // от какой стоковой спускаемся (0..1)
        string _template;   // текущий .bat-шаблон
        string _championArgs, _controlArgs;
        Config _champion;
        string _championName = AutoTune.OutputName;
        readonly List<(string Base, string Args)> _roundBases = new List<(string, string)>();
        readonly List<(string Template, string Args, Config Cfg)> _descentWinners =
            new List<(string, string, Config)>();
        readonly List<string> _templates = new List<string>();
        readonly List<Config> _ranked = new List<Config>();
        readonly HashSet<string> _resultsBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _saved;
        bool _finalDone;
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
            Closed += (s, e) => { Ctx.AutoTuneActive = false; AutoTune.SetTuneLock(false); StopProcess(); AutoTune.CleanCandidates(); CleanupResults(_finalDone); };
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

        // Промежуточные файлы результатов фаз подбора пользователю не нужны — убираем.
        // Остаётся только отчёт финальной проверки (keepNewest — после неё)
        void CleanupResults(bool keepNewest)
        {
            try
            {
                FileInfo newest = null;
                if (keepNewest)
                    newest = ResultsModel.GetResultFiles().OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                foreach (var f in ResultsModel.GetResultFiles())
                    if (!_resultsBefore.Contains(f.FullName)
                        && (newest == null || !string.Equals(f.FullName, newest.FullName, StringComparison.OrdinalIgnoreCase)))
                        try { File.Delete(f.FullName); } catch { }
            }
            catch { }
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
            AutoTune.SetTuneLock(true); // все процессы zapret UI видят, что подбор идёт
            _resultsBefore.Clear();
            _saved = false;
            _finalDone = false;
            foreach (var f in ResultsModel.GetResultFiles()) _resultsBefore.Add(f.FullName);

            _phase = PhaseKind.PreTest;
            _phaseStarted = DateTime.Now;
            _roundsTotal = 1 + 2 * AutoTune.Axes.Length * 2 + 1; // предтест + два спуска + валидация
            _roundsDone = 0;

            var answers = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(selected)) };
            BeginRun(answers, "Предтест: проверяю " + selected.Count + " стратегий…");
        }

        void BeginRun(List<string> answers, string phaseText)
        {
            PhaseText.Text = phaseText;
            Progress.Value = 100.0 * _roundsDone / Math.Max(1, _roundsTotal);
            AppendLog("=== " + phaseText);
            foreach (var c in _roundBases)
            {
                string p = Path.Combine(Ctx.Root, AutoTune.CandidatePrefix + c.Base + ".bat");
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
                CheckConfigAlignment();
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
        void CheckConfigAlignment()
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

        static double Rate(Config c) => c.Total > 0 ? (double)c.Ok / c.Total : 0;

        static int Compare(Config a, Config b, string argsA, string argsB)
        {
            if (Math.Abs(Rate(a) - Rate(b)) > 0.001) return Rate(a) > Rate(b) ? -1 : 1;
            if (a.PingFail != b.PingFail) return a.PingFail < b.PingFail ? -1 : 1;
            return AutoTune.CostOf(argsA).CompareTo(AutoTune.CostOf(argsB));
        }

        // худший результат пары прогонов: меньше прошедших проверок, больше провалов пинга
        static Config WorstOf(IEnumerable<Config> runs)
            => runs.OrderBy(c => Rate(c)).ThenByDescending(c => c.PingFail).First();

        void CompletePhase()
        {
            ReadLog();
            var results = ReadLatestResults();
            _roundsDone++;

            if (_phase == PhaseKind.PreTest)
            {
                var ranked = results
                    .Where(c => !c.Name.StartsWith(AutoTune.CandidatePrefix) && c.Name != Ctx.BaselineName)
                    .OrderByDescending(Rate)
                    .ThenBy(c => c.PingFail)
                    .ToList();
                if (ranked.Count == 0) throw new Exception("Предтест не дал результатов.");
                _ranked.Clear();
                _ranked.AddRange(ranked);
                _templates.Clear();
                _templates.Add(ranked[0].Name);
                AppendLog($"Лучшая стоковая: {ranked[0].Name} ({ranked[0].Ok}/{ranked[0].Total}). Спуск от неё.");
                _startIdx = 0;
                BeginDescent();
                return;
            }

            if (_phase == PhaseKind.Descent)
            {
                foreach (var (b, args) in _roundBases)
                {
                    var run = results.FirstOrDefault(r => r.Name == AutoTune.CandidatePrefix + b);
                    if (run == null) continue;
                    if (Compare(run, _champion, args, _championArgs) < 0)
                    {
                        _champion = run;
                        _championArgs = args;
                        AppendLog($"→ улучшение: {run.Ok}/{run.Total} · {AutoTune.Describe(args)}");
                    }
                }
                AppendLog("→ раунд отработан.");
                NextAxisOrFinish();
                return;
            }

            if (_phase == PhaseKind.Validate)
            {
                var champRuns = _roundBases
                    .Where(b => b.Base.StartsWith("final"))
                    .Select(b => results.FirstOrDefault(r => r.Name == AutoTune.CandidatePrefix + b.Base))
                    .Where(c => c != null).ToList();
                if (!champRuns.Any()) throw new Exception("Валидация не дала результатов.");
                var control = results.FirstOrDefault(r => r.Name == _template);
                var worst = WorstOf(champRuns);
                bool ok = control == null || Compare(worst, control, _championArgs, _controlArgs) <= 0;
                if (!ok)
                {
                    _championArgs = _controlArgs;
                    _champion = control ?? _champion;
                    _championName = _template + " (стоковая)";
                    AppendLog("Подобранный вариант не подтвердился — оставляем стоковую.");
                }
                else
                {
                    AppendLog($"Валидация пройдена: худший из {champRuns.Count} прогонов {worst.Ok}/{worst.Total}.");
                }
                PhaseDone(null);
                return;
            }

            if (_phase == PhaseKind.FinalTest)
            {
                var mine = results.FirstOrDefault(r => r.Name == AutoTune.OutputName);
                var stock = results.FirstOrDefault(r => r.Name == _template);
                if (mine != null)
                    AppendLog($"Финальная проверка: {AutoTune.OutputName} — {mine.Ok}/{mine.Total}" +
                        (stock != null ? $", {_template} — {stock.Ok}/{stock.Total}" : "") + ".");
                _finalDone = true;
                PhaseDone(null);
                // Показать таблицу результатов в главном окне
                if (Ctx.Main != null)
                {
                    Ctx.Main.Tabs.SelectedIndex = 0;
                    Ctx.Main.LoadResultFiles();
                }
            }
        }

        void BeginDescent()
        {
            _template = _templates[_startIdx];
            _championArgs = Zapret.GetBatArgs(Path.Combine(Ctx.Root, _template + ".bat"));
            // чемпионом старта — реальный результат шаблона из предтеста
            _champion = _ranked.First(c => c.Name == _template);
            _controlArgs = _championArgs;
            _phase = PhaseKind.Descent;
            _axisIdx = -1;
            _pass = 1;
            AppendLog($"=== Спуск от {_template} (старт {_champion.Ok}/{_champion.Total})");
            NextAxisOrFinish();
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
                _descentWinners.Add((_template, _championArgs, _champion));
                _startIdx++;
                if (_startIdx < _templates.Count)
                {
                    BeginDescent();
                    return;
                }
                // финальный заезд: лучший из победителей спусков
                var best = _descentWinners.OrderByDescending(w => Rate(w.Cfg)).ThenBy(w => w.Cfg.PingFail).First();
                _template = best.Template;
                _championArgs = best.Args;
                _champion = best.Cfg;
                _controlArgs = Zapret.GetBatArgs(Path.Combine(Ctx.Root, _template + ".bat"));
                _phase = PhaseKind.Validate;
                _roundBases.Clear();
                AutoTune.CleanCandidates();
                var names = new List<string>();
                for (int i = 1; i <= 2; i++)
                {
                    string n = $"final-{i}";
                    AutoTune.WriteCandidate(n, _championArgs);
                    _roundBases.Add((n, _championArgs));
                    names.Add(AutoTune.CandidatePrefix + n);
                }
                names.Add(_template);
                var answers = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(names)) };
                BeginRun(answers, $"Валидация: чемпион (два прогона) против {_template}…");
                return;
            }
            var (title, gen) = AutoTune.Axes[_axisIdx];
            var variants = gen(_championArgs)
                .Where(v => v != _championArgs)
                .Distinct() // дедупликация по самим аргументам: Describe не различает seqovl/пейлоад
                .Take(4)
                .ToList();
            if (variants.Count == 0) { NextAxisOrFinish(); return; }
            AutoTune.CleanCandidates();
            _roundBases.Clear();
            var candNames = new List<string>();
            for (int i = 0; i < variants.Count; i++)
            {
                string b = $"a{_axisIdx}p{_pass}-{i}";
                AutoTune.WriteCandidate(b, variants[i]);
                _roundBases.Add((b, variants[i]));
                candNames.Add(AutoTune.CandidatePrefix + b);
            }
            var ans = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(candNames)) };
            BeginRun(ans, $"Спуск, проход {_pass}: ось «{title}» ({variants.Count} вариантов)");
        }

        void PhaseDone(string note)
        {
            _phase = PhaseKind.Done;
            // Флаг подбора и замок НЕ снимаем: после сохранения идёт финальная проверка,
            // и главное окно вернуло бы службу посреди неё. Снятие — только в Closed
            _timer.Stop();
            Progress.Value = 100;
            StrategiesCard.IsEnabled = true;
            if (note != null) PhaseText.Text = note;
            else PhaseText.Text = "Готово: " + _championName;
            if (!_saved) BtnSave.Visibility = Visibility.Visible;
            BtnStart.IsEnabled = true;
            BtnStart.Content = "Начать заново";
            AutoTune.CleanCandidates();
            CleanupResults(_finalDone);
            AppendLog("");
            if (_champion != null && _champion.Total > 0)
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
            string newContent = ReplaceWinwsBlock(content, _championArgs);
            if (newContent == content)
                newContent = content.Replace(Zapret.GetBatArgs(templatePath), _championArgs);
            string outp = Path.Combine(Ctx.Root, AutoTune.OutputName + ".bat");
            File.WriteAllText(outp, newContent);
            _saved = true;
            AppendLog("Сохранено как «" + AutoTune.OutputName + "».");
            // Финальная проверка подобранного против стоковой — её отчёт остаётся в результатах
            BtnSave.Visibility = Visibility.Collapsed;
            StrategiesCard.IsEnabled = false;
            BtnStart.IsEnabled = false;
            _phase = PhaseKind.FinalTest;
            _roundBases.Clear();
            AutoTune.CleanCandidates();
            var names = new List<string> { AutoTune.OutputName, _template };
            var answers = new List<string> { "1", "2", string.Join(",", AutoTune.IndicesOf(names)) };
            _roundsDone = _roundsTotal - 1;
            BeginRun(answers, $"Финальная проверка: {AutoTune.OutputName} против {_template}…");
        }

        // Заменяет весь блок запуска winws (строка start + все строки продолжения через ^)
        // одной строкой с новыми аргументами — иначе старые continuation-строки остаются
        // и конфиг дублируется
        static string ReplaceWinwsBlock(string content, string newArgs)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            int startIdx = lines.FindIndex(l => l.Contains("start \"zapret:") && l.Contains("winws.exe"));
            if (startIdx < 0) return content;
            int endIdx = startIdx;
            while (endIdx < lines.Count && lines[endIdx].TrimEnd().EndsWith("^", StringComparison.Ordinal))
                endIdx++;
            lines[startIdx] = "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" " + newArgs;
            if (endIdx > startIdx) lines.RemoveRange(startIdx + 1, endIdx - startIdx);
            return string.Join("\r\n", lines);
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
