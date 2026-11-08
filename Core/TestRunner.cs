using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretUi
{
    public class TestRun
    {
        public string Kind;
        public List<string> Phases;
        public int Phase;
        public List<string> AnswerTail;
        public RedirectedProcess Proc;
        public string Type;
        public Dictionary<string, string> Groups;
        public long Pos;
        public string Partial = "";
        public StringBuilder Log = new StringBuilder();
        public bool LogChanged;
        public int Index;
        public int Total;
        public Config Current;
        public Target DpiTarget;
        public List<Config> Done = new List<Config>();
        public List<Config> PrevDone = new List<Config>();
        public List<string> Errors = new List<string>();
        public bool Changed;
        public bool ShownCurrent;
        public bool Finished;
        public bool Cancelled;
        public DateTime Started;
        public DateTime PhaseStarted;
        public string ResultFile;
        public List<string> ResultFiles = new List<string>();
        public DateTime? ConfigStartedAt;
        public List<double> Durations = new List<double>();
    }

    // Запуск и разбор хода test zapret.ps1. Окно читает его лог по ходу и раскладывает в таблицу.
    public static class TestRunner
    {
        // Секунд на одну стратегию по типу теста и разгон скрипта перед первой стратегией
        static readonly Dictionary<string, double> PaceDefault = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["standard"] = 8, ["dpi"] = 14 };
        static readonly Dictionary<string, double> Overhead = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["standard"] = 4, ["dpi"] = 8 };

        public static List<string> GetTestPhases(string kind) => kind == "combined" ? new List<string> { "standard", "dpi" } : new List<string> { kind };

        public static double GetTestPace(string type)
        {
            if (Ctx.UiState.TestPace.TryGetValue(type, out double v) && v > 0) return v;
            return PaceDefault.TryGetValue(type, out double d) ? d : 8;
        }

        // Средний темп прошедшего этапа смешиваем с прежним, чтобы один необычный прогон не сбивал прогноз
        public static void UpdateTestPace(string type, List<double> durations)
        {
            if (durations.Count == 0) return;
            double avg = durations.Average();
            double prev = Ctx.UiState.TestPace.TryGetValue(type, out double p) ? p : 0;
            Ctx.UiState.TestPace[type] = Math.Round(prev > 0 ? (prev + avg) / 2 : avg, 1);
            Ctx.UiState.Save();
        }

        public static double GetTestEstimate(string kind, int count)
        {
            double sec = 0;
            foreach (var t in GetTestPhases(kind)) sec += Overhead[t] + count * GetTestPace(t);
            return sec;
        }

        public static string FormatEstimate(double sec)
        {
            if (sec < 45) return "меньше минуты";
            return $"~{Math.Max(1, (int)Math.Round(sec / 60))} мин";
        }

        // Осталось секунд: текущий этап — по темпу этого прогона, следующие — по запомненному
        public static double GetTestRemaining(TestRun r)
        {
            string type = r.Phases[r.Phase];
            double pace = r.Durations.Count > 0 ? r.Durations.Average() : GetTestPace(type);
            var now = DateTime.Now;
            double left = 0;
            if (r.Index == 0)
            {
                left += Math.Max(0, Overhead[type] - (now - r.PhaseStarted).TotalSeconds) + r.Total * pace;
            }
            else
            {
                left += Math.Max(0, r.Total - r.Done.Count - (r.Current != null ? 1 : 0)) * pace;
                if (r.Current != null && r.ConfigStartedAt.HasValue)
                    left += Math.Max(0, pace - (now - r.ConfigStartedAt.Value).TotalSeconds);
            }
            for (int i = r.Phase + 1; i < r.Phases.Count; i++)
            {
                string t = r.Phases[i];
                left += Overhead[t] + r.Total * GetTestPace(t);
            }
            return left;
        }

        public static string FormatElapsed(TimeSpan t) => $"{(int)Math.Floor(t.TotalMinutes)}:{t.Seconds:00}";

        // ---------- log parsing ----------

        // Разбор одной строки вывода теста
        public static void AddTestLine(string line)
        {
            var r = Ctx.Run;
            r.Log.AppendLine(line);

            var m = Regex.Match(line, "^\\s*\\[(\\d+)/(\\d+)\\]\\s+(.+?)\\.bat\\s*$");
            if (m.Success)
            {
                CompleteTestConfig();
                r.Index = int.Parse(m.Groups[1].Value);
                r.Total = int.Parse(m.Groups[2].Value);
                r.ConfigStartedAt = DateTime.Now;
                r.Current = ResultsModel.NewConfig(m.Groups[3].Value, r.Type);
                r.Current.Pending = true;
                r.DpiTarget = null;
                return;
            }
            if (line.StartsWith("All tests finished", StringComparison.Ordinal)) { CompleteTestConfig(); return; }
            var rm = Regex.Match(line, "Results saved to (.+?)\\s*$");
            if (rm.Success) { r.ResultFile = rm.Groups[1].Value; return; }
            if (line.TrimStart().StartsWith("[ERROR]", StringComparison.Ordinal)) r.Errors.Add(line.Trim());

            var cur = r.Current;
            if (cur == null) return;
            if (line.Contains("Strategy failed to start")) { cur.Failed = true; return; }

            if (r.Type == "standard")
            {
                var sm = Regex.Match(line, "^\\s{2}(\\w+)\\s+(.*?)\\s*\\|?\\s*Ping:\\s*(.+?)\\s*$");
                if (!sm.Success) return;
                var tokens = new List<Token>();
                foreach (Match t in Regex.Matches(sm.Groups[2].Value, "(HTTP|TLS1\\.2|TLS1\\.3):(\\w+)"))
                    tokens.Add(new Token { Label = t.Groups[1].Value, Code = t.Groups[2].Value });
                string key = sm.Groups[1].Value;
                string group = r.Groups.TryGetValue(key, out var g) ? g : "Другое";
                string ping = sm.Groups[3].Value;
                if (ping == "n/a") ping = "";
                cur.Targets.Add(new Target { Key = key, Group = group, Tokens = tokens, Ping = ping });
            }
            else
            {
                var dm = Regex.Match(line, "^=== \\[(.*?)\\]\\[(.*?)\\] (.+?) ===");
                if (dm.Success)
                {
                    r.DpiTarget = new Target { Key = $"[{dm.Groups[1].Value}] {dm.Groups[3].Value} ({dm.Groups[2].Value})", Group = "DPI" };
                    cur.Targets.Add(r.DpiTarget);
                    return;
                }
                var cm = Regex.Match(line, "^\\[(.+?)\\] code=.*status=(\\S+)");
                if (cm.Success && r.DpiTarget != null)
                    r.DpiTarget.Tokens.Add(new Token { Label = cm.Groups[1].Value, Code = cm.Groups[2].Value });
            }
        }

        // Стратегия проверена: переносим её в таблицу
        public static void CompleteTestConfig()
        {
            var r = Ctx.Run;
            if (r.Current == null) return;
            r.Current.Pending = false;
            ResultsModel.CompleteConfig(r.Current);
            r.Done.Add(r.Current);
            r.Current = null;
            r.Changed = true;
            if (r.ConfigStartedAt.HasValue)
            {
                r.Durations.Add((DateTime.Now - r.ConfigStartedAt.Value).TotalSeconds);
                r.ConfigStartedAt = null;
            }
        }

        public static void ReadTestLog()
        {
            var r = Ctx.Run;
            if (!File.Exists(Ctx.TestLog)) return;
            byte[] buf;
            using (var fs = new FileStream(Ctx.TestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length <= r.Pos) return;
                fs.Position = r.Pos;
                buf = new byte[fs.Length - r.Pos];
                int n = fs.Read(buf, 0, buf.Length);
                if (buf.Length != n) Array.Resize(ref buf, n);
                r.Pos += n;
            }
            var parts = Regex.Split(r.Partial + Oem.Decode(buf), "\r?\n");
            r.Partial = parts[parts.Length - 1];
            for (int i = 0; i < parts.Length - 1; i++) AddTestLine(parts[i]);
            r.LogChanged = true;
        }

        // ---------- run control ----------

        // Ответы на вопросы скрипта (тип теста + выбор стратегий) подаются через stdin из файла
        public static void StartTestPhase(TestRun r)
        {
            string type = r.Phases[r.Phase];
            var answers = new List<string> { type == "dpi" ? "2" : "1" };
            answers.AddRange(r.AnswerTail);
            File.WriteAllText(Ctx.TestInput, string.Join("\r\n", answers) + "\r\n");
            try { File.Delete(Ctx.TestLog); } catch { }
            try { File.Delete(Ctx.TestLog + ".err"); } catch { }
            r.Type = type;
            r.Pos = 0; r.Partial = ""; r.Index = 0; r.Current = null; r.DpiTarget = null; r.ResultFile = null;
            r.Done = new List<Config>();
            r.Durations = new List<double>();
            r.ConfigStartedAt = null;
            r.PhaseStarted = DateTime.Now;
            r.Proc = RedirectedProcess.Start("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Ctx.TestPs}\"", Ctx.UtilsDir,
                Ctx.TestInput, Ctx.TestLog, Ctx.TestLog + ".err");
        }

        // Индексы выбранных стратегий по тому же списку .bat, что у теста (вместе с «Без обхода»)
        public static Strategy[] AllBatFilesIncludingBaseline()
        {
            return Directory.GetFiles(Ctx.Root, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Select(f => new Strategy { File = new FileInfo(f), Name = Path.GetFileNameWithoutExtension(f) })
                .OrderBy(s => Str.NaturalKey(s.Name), StringComparer.Ordinal)
                .ToArray();
        }

        public static void StopTestRun()
        {
            var r = Ctx.Run;
            if (r == null || r.Finished) return;
            r.Cancelled = true;
            r.Proc?.KillTree();
            Proc.KillByName("winws");
            // То же, что делает сам тест в finally: вернуть ipset, переключённый для DPI-теста
            string bk = Path.Combine(Ctx.ListsDir, "ipset-all.test-backup.txt");
            if (File.Exists(bk)) File.Move(bk, Ctx.IpsetFile, true);
            try { File.Delete(Path.Combine(Ctx.Root, "ipset_switched.flag")); } catch { }
        }

        public static void DisposeRun(TestRun r)
        {
            try { r?.Proc?.Dispose(); } catch { }
        }
    }
}
