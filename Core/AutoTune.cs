using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZapretUi
{
    // Автоподбор конфигурации из стоковых стратегий Flowseal:
    // кандидаты — копии стокового .bat с одним изменённым «циферблатом»,
    // прогоняются штатным test zapret.ps1 партиями по номерам.
    public static class AutoTune
    {
        public const string CandidatePrefix = "~auto-";
        public const string OutputName = "custom (AUTO)";

        // ---------- работа с аргументами ----------

        static string Pattern(string key) => @"(--" + Regex.Escape(key) + @"=)(\S+)";

        public static string GetArg(string args, string key)
        {
            var m = Regex.Match(args, Pattern(key));
            return m.Success ? m.Groups[2].Value : null;
        }

        // Заменить значение ключа или добавить его перед первым --dpi-desync
        public static string SetArg(string args, string key, string value)
        {
            var rx = new Regex(Pattern(key));
            if (rx.IsMatch(args)) return rx.Replace(args, "${1}" + value);
            int i = args.IndexOf("--dpi-desync", StringComparison.Ordinal);
            string ins = " --" + key + "=" + value;
            return i >= 0 ? args.Insert(i, ins) : args + ins;
        }

        public static string RemoveArg(string args, string key)
            => Regex.Replace(args, @"\s*" + Pattern(key), "");

        // ---------- оси спуска: варианты одного параметра ----------

        // repeats: минимум (1), текущее пополам (но ≥1), текущее ×2
        public static List<string> AxisRepeats(string args)
        {
            int cur = 1;
            var m = Regex.Match(args, @"--dpi-desync-repeats=(\d+)");
            if (m.Success) cur = int.Parse(m.Groups[1].Value);
            var set = new HashSet<int> { 1, Math.Max(1, cur / 2), cur * 2 };
            return set.Where(r => r != cur).OrderBy(r => r).Select(r => SetArg(args, "dpi-desync-repeats", r.ToString())).ToList();
        }

        // method: упрощения вокруг текущего
        public static List<string> AxisMethod(string args)
        {
            var variants = new List<string>();
            foreach (var method in new[] { "fake,split2", "fake,split", "disorder2", "multisplit" })
                if (GetArg(args, "dpi-desync") != method)
                    variants.Add(SetArg(args, "dpi-desync", method));
            return variants;
        }

        // fooling: ts / badsum / без fooling
        public static List<string> AxisFooling(string args)
        {
            var cur = GetArg(args, "dpi-desync-fooling");
            var variants = new List<string>();
            foreach (var f in new[] { "ts", "badsum" })
                if (cur != f) variants.Add(SetArg(args, "dpi-desync-fooling", f));
            if (cur != null) variants.Add(RemoveArg(args, "dpi-desync-fooling"));
            return variants;
        }

        // split-pos: 1 / 2 / midsni (актуально для методов с split)
        public static List<string> AxisSplitPos(string args)
        {
            var cur = GetArg(args, "dpi-desync-split-pos");
            var variants = new List<string>();
            foreach (var p in new[] { "1", "2", "midsni" })
                if (cur != p) variants.Add(SetArg(args, "dpi-desync-split-pos", p));
            return variants;
        }

        // seqovl: шаг наложения паттерна для multisplit/hostfakesplit
        public static List<string> AxisSeqovl(string args)
        {
            var m = Regex.Match(args, @"--dpi-desync-split-seqovl=(\d+)");
            if (!m.Success) return new List<string>();
            int cur = int.Parse(m.Groups[1].Value);
            var set = new HashSet<int>();
            foreach (var d in new[] { -256, -128, 128, 256 })
            {
                int v = cur + d;
                if (v >= 1) set.Add(v);
            }
            return set.OrderBy(v => Math.Abs(v - cur)).Take(4)
                .Select(v => SetArg(args, "dpi-desync-split-seqovl", v.ToString())).ToList();
        }

        // Смена «личности» фейка: подставляем другой .bin той же серии из библиотеки bin\
        public static List<string> AxisPayload(string args)
        {
            var variants = new List<string>();
            if (!Directory.Exists(Ctx.BinDir)) return variants;
            var m = Regex.Match(args, @"--dpi-desync-(fake-tls|fake-http|fake-quic)=\S*?([\w.-]+\.bin)");
            if (!m.Success) return variants;
            string key = "dpi-desync-" + m.Groups[1].Value;
            string currentName = m.Groups[2].Value;
            var pm = Regex.Match(currentName, "^(tls_clienthello|quic_initial|stun)");
            if (!pm.Success) return variants;
            foreach (var f in Directory.GetFiles(Ctx.BinDir, pm.Groups[1].Value + "_*.bin"))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)) continue;
                variants.Add(new Regex(Pattern(key)).Replace(args, "${1}" + name));
                if (variants.Count >= 4) break;
            }
            return variants;
        }

        // Порядок осей: новые и самые перспективные — первыми
        public static readonly (string Title, Func<string, List<string>> Gen)[] Axes =
        {
            ("пейлоад фейка", AxisPayload),
            ("seqovl", AxisSeqovl),
            ("метод десинка", AxisMethod),
            ("repeats", AxisRepeats),
            ("fooling", AxisFooling),
            ("позиция сплита", AxisSplitPos),
        };

        // ---------- кандидаты как .bat ----------

        // Тело кандидата: преамбула как у стоковых стратегий (переменные GameFilter)
        public static string CandidateBat(string argLine)
        {
            return "@echo off\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "call service.bat load_game_filter\r\n" +
                "call service.bat load_user_lists\r\n" +
                "set \"BIN=%~dp0bin\\\"\r\n" +
                "set \"LISTS=%~dp0lists\\\"\r\n" +
                "cd /d %BIN%\r\n" +
                "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" " + argLine + "\r\n";
        }

        public static string WriteCandidate(string name, string argLine)
        {
            string path = Path.Combine(Ctx.Root, CandidatePrefix + name + ".bat");
            File.WriteAllText(path, CandidateBat(argLine));
            return path;
        }

        public static void CleanCandidates()
        {
            if (!Directory.Exists(Ctx.Root)) return;
            foreach (var f in Directory.GetFiles(Ctx.Root, CandidatePrefix + "*.bat"))
                try { File.Delete(f); } catch { }
        }

        // Позиции имён в том же естественном порядке, что использует test zapret.ps1
        public static List<int> IndicesOf(IEnumerable<string> names)
        {
            var all = Directory.GetFiles(Ctx.Root, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(n => n, Str.NaturalComparer)
                .ToList();
            var idx = new List<int>();
            foreach (var n in names)
            {
                int i = all.IndexOf(n + ".bat");
                if (i >= 0) idx.Add(i + 1); // нумерация в тесте с 1
            }
            return idx;
        }

        // ---------- оценка ----------

        public static int CostOf(string args)
        {
            int repeats = 1;
            var m = Regex.Match(args, @"--dpi-desync-repeats=(\d+)");
            if (m.Success) repeats = int.Parse(m.Groups[1].Value);
            return repeats;
        }

        public static string Describe(string args)
        {
            var method = GetArg(args, "dpi-desync") ?? "?";
            var fooling = GetArg(args, "dpi-desync-fooling") ?? "нет";
            var pos = GetArg(args, "dpi-desync-split-pos") ?? "-";
            var repeats = GetArg(args, "dpi-desync-repeats") ?? "1";
            var seqovl = GetArg(args, "dpi-desync-split-seqovl");
            var m = Regex.Match(args, @"--dpi-desync-(?:fake-tls|fake-http|fake-quic)=\S*?([\w.-]+\.bin)");
            var payload = m.Success ? Path.GetFileNameWithoutExtension(m.Groups[1].Value) : null;
            string s = $"method={method}, fooling={fooling}, split-pos={pos}, repeats={repeats}";
            if (seqovl != null) s += $", seqovl={seqovl}";
            if (payload != null) s += $", payload={payload}";
            return s;
        }

        // Маркер подбора — общий для всех процессов zapret UI: пока файл есть, ни одно
        // окно не возвращает службу (иначе второй процесс возвращает её посреди чужого подбора)
        public static string TuneLockFile => Path.Combine(Ctx.UtilsDir, "autotune.lock");

        public static bool TuneLockActive
        {
            get
            {
                try
                {
                    if (!File.Exists(TuneLockFile)) return false;
                    // Зависший маркер старше 3 часов не считаем
                    return (DateTime.Now - File.GetLastWriteTime(TuneLockFile)).TotalHours < 3;
                }
                catch { return false; }
            }
        }

        public static void SetTuneLock(bool on)
        {
            try
            {
                if (on) File.WriteAllText(TuneLockFile, DateTime.Now.ToString("s"));
                else if (File.Exists(TuneLockFile)) File.Delete(TuneLockFile);
            }
            catch { }
        }
    }
}
