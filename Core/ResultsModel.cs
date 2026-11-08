using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZapretUi
{
    public class Token
    {
        public string Label;
        public string Code;
    }

    public class Target
    {
        public string Key;
        public string Group;
        public List<Token> Tokens = new List<Token>();
        public string Ping;
    }

    public class Config
    {
        public string Name;
        public string Type;
        public List<Target> Targets = new List<Target>();
        public int Ok;
        public int Total;
        public int? PingAvg;
        public int PingFail;
        public bool Pending;
        public bool Failed;
    }

    public class ResultSet
    {
        public List<Config> Configs;
        public string Best;
        public List<string> Groups;
    }

    public static class ResultsModel
    {
        public static Config NewConfig(string name, string type) => new Config { Name = name, Type = type };

        // Считает итог стратегии по её целям
        public static void CompleteConfig(Config cfg)
        {
            cfg.Ok = 0; cfg.Total = 0; cfg.PingFail = 0;
            var pings = new List<int>();
            foreach (var t in cfg.Targets)
            {
                foreach (var tok in t.Tokens)
                {
                    cfg.Total++;
                    if (tok.Code == "OK") cfg.Ok++;
                }
                var pm = Regex.Match(t.Ping ?? "", "^(\\d+)\\s*ms");
                if (pm.Success) pings.Add(int.Parse(pm.Groups[1].Value));
                else if (!string.IsNullOrEmpty(t.Ping)) cfg.PingFail++;
            }
            cfg.PingAvg = pings.Count > 0 ? (int)Math.Round(pings.Average()) : (int?)null;
        }

        // Обычный + DPI: по каждой стратегии одна строка с проверками обоих тестов
        public static List<Config> MergeTestConfigs(IEnumerable<Config> configs)
        {
            var byName = new Dictionary<string, Config>(StringComparer.Ordinal);
            foreach (var c in configs)
            {
                if (!byName.TryGetValue(c.Name, out var m)) { m = NewConfig(c.Name, c.Type); byName[c.Name] = m; }
                if (m.Type != c.Type) m.Type = "combined";
                m.Targets.AddRange(c.Targets);
                if (c.Pending) m.Pending = true;
                if (c.Failed) m.Failed = true;
            }
            foreach (var m in byName.Values) CompleteConfig(m);
            return byName.Values.ToList();
        }

        public static string TestTypeName(string type)
        {
            switch (type)
            {
                case "dpi": return "DPI";
                case "combined": return "обычный + DPI";
                default: return "обычный";
            }
        }

        // Группы из «Настроек теста» (targets.txt) по порядку; группы только с пингом колонкой не показываем
        public static List<string> TargetGroupOrder()
        {
            if (!File.Exists(Ctx.TargetsTxt)) return new List<string>();
            return Targets.ReadTargetsModel(Ctx.TargetsTxt).Groups
                .Where(g => g.Items.Any(i => !i.Value.StartsWith("PING:", StringComparison.Ordinal)))
                .Select(g => g.Name).ToList();
        }

        public static ResultSet NewResultSet(IEnumerable<Config> configs, string best, string kind = "standard")
        {
            var groupOrder = kind != "dpi" ? TargetGroupOrder() : new List<string>();
            foreach (var cfg in configs)
                foreach (var t in cfg.Targets)
                    if (t.Tokens.Count > 0 && !groupOrder.Contains(t.Group)) groupOrder.Add(t.Group);
            // Колонка DPI — всегда последней
            if ((kind == "dpi" || kind == "combined" || groupOrder.Contains("DPI")))
            {
                groupOrder = groupOrder.Where(g => g != "DPI").ToList();
                groupOrder.Add("DPI");
            }
            var sorted = configs.OrderByDescending(c => c.Total > 0 ? (double)c.Ok / c.Total : 0)
                .ThenBy(c => c.PingAvg ?? 9999).ToList();
            // «Без обхода» — только для сравнения: лучшей стратегией её не считаем
            if (best == Ctx.BaselineName) best = null;
            var candidates = sorted.Where(c => c.Name != Ctx.BaselineName).ToList();
            if (best == null && candidates.Count > 0 && candidates[0].Total > 0) best = candidates[0].Name;
            // Лучшая (по мнению теста) — всегда первой строкой
            if (best != null)
            {
                var top = sorted.Where(c => c.Name == best).ToList();
                if (top.Count > 0) sorted = top.Concat(sorted.Where(c => c.Name != best)).ToList();
            }
            return new ResultSet { Configs = sorted, Best = best, Groups = groupOrder };
        }

        // ---------- result files ----------

        public static FileInfo[] GetResultFiles()
        {
            if (!Directory.Exists(Ctx.ResultsDir)) return Array.Empty<FileInfo>();
            try
            {
                return new DirectoryInfo(Ctx.ResultsDir).GetFiles("test_results_*.txt")
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            }
            catch { return Array.Empty<FileInfo>(); }
        }

        public static string FormatResultFileName(FileInfo file)
        {
            var m = Regex.Match(file.Name, "test_results_(\\d{4})-(\\d\\d)-(\\d\\d)_(\\d\\d)-(\\d\\d)");
            string name = m.Success
                ? $"{m.Groups[3].Value}.{m.Groups[2].Value}.{m.Groups[1].Value}  {m.Groups[4].Value}:{m.Groups[5].Value}"
                : file.Name;
            return name + "  · " + TestTypeName(GetResultFileKind(file));
        }

        // Тип теста в файле результатов: комбинированный — по имени, иначе по первой строке Config:
        public static string GetResultFileKind(FileInfo file)
        {
            if (file.Name.Contains("_combined")) return "combined";
            string key = file.FullName + "|" + file.LastWriteTimeUtc.Ticks;
            if (Ctx.ResultKinds.TryGetValue(key, out var cached)) return cached;
            string kind = "standard";
            try
            {
                foreach (var line in File.ReadLines(file.FullName))
                {
                    var m = Regex.Match(line, "^Config: .* \\(Type: (\\w+)\\)");
                    if (m.Success) { kind = m.Groups[1].Value; break; }
                }
            }
            catch { }
            Ctx.ResultKinds[key] = kind;
            return kind;
        }

        public static string JoinTestResultFiles(IEnumerable<FileInfo> files)
        {
            var list = files.ToList();
            string outp = Path.Combine(Ctx.ResultsDir, Path.GetFileNameWithoutExtension(list[0].Name) + "_combined.txt");
            var lines = new List<string> { "# zapret-ui: combined test (standard + dpi)" };
            foreach (var f in list)
            {
                lines.Add("");
                lines.AddRange(File.ReadAllLines(f.FullName));
            }
            Fs.WriteTextFile(outp, lines.ToArray());
            foreach (var f in list) { try { File.Delete(f.FullName); } catch { } }
            return outp;
        }

        public static ResultSet ReadTestResults(string path)
        {
            var groups = Targets.GetTargetGroups();
            var configs = new List<Config>();
            string best = null, bestStandard = null;
            Config cur = null;
            Target curDpiTarget = null;
            foreach (var line in File.ReadLines(path))
            {
                var m = Regex.Match(line, "^Config: (.+?)(?:\\.bat)? \\(Type: (\\w+)\\)");
                if (m.Success)
                {
                    cur = NewConfig(m.Groups[1].Value, m.Groups[2].Value);
                    configs.Add(cur);
                    continue;
                }
                if (line.StartsWith("=== ANALYTICS", StringComparison.Ordinal)) { cur = null; continue; }
                var bm = Regex.Match(line, "^Best strategy: (.+?)(?:\\.bat)?\\s*$");
                if (bm.Success)
                {
                    // В комбинированном файле два итога; лучшая — по обычному тесту
                    if (configs.Count > 0 && configs[configs.Count - 1].Type == "standard") bestStandard = bm.Groups[1].Value;
                    best = bm.Groups[1].Value;
                    continue;
                }
                if (cur == null) continue;

                if (cur.Type == "standard")
                {
                    var tm = Regex.Match(line, "^\\s+(\\w+) : (.*?)\\s*\\| Ping: (.*)$");
                    if (!tm.Success) continue;
                    string key = tm.Groups[1].Value;
                    var tokens = new List<Token>();
                    foreach (var tok in Regex.Split(tm.Groups[2].Value.Trim(), "\\s+").Where(x => x.Length > 0))
                    {
                        int colon = tok.IndexOf(':');
                        tokens.Add(new Token
                        {
                            Label = colon > 0 ? tok.Substring(0, colon) : tok,
                            Code = colon > 0 ? tok.Substring(colon + 1) : "",
                        });
                    }
                    string ping = tm.Groups[3].Value.Trim();
                    string group = groups.TryGetValue(key, out var g) ? g : "Другое";
                    cur.Targets.Add(new Target { Key = key, Group = group, Tokens = tokens, Ping = ping });
                }
                else
                {
                    var t = Regex.Match(line, "^\\s+Target: (.+)$");
                    if (t.Success)
                    {
                        curDpiTarget = new Target { Key = t.Groups[1].Value, Group = "DPI" };
                        cur.Targets.Add(curDpiTarget);
                        continue;
                    }
                    var d = Regex.Match(line, "^\\s+(\\S+): code=.*status=(\\S+)");
                    if (d.Success && curDpiTarget != null)
                        curDpiTarget.Tokens.Add(new Token { Label = d.Groups[1].Value, Code = d.Groups[2].Value });
                }
            }

            foreach (var c in configs) CompleteConfig(c);
            var types = configs.Select(c => c.Type).Distinct().ToList();
            if (types.Count > 1)
            {
                // Комбинированный тест: одна строка на стратегию, «Лучшая» — как у обычного теста
                if (bestStandard == null)
                    bestStandard = NewResultSet(configs.Where(c => c.Type == "standard").ToList(), null, "standard").Best;
                return NewResultSet(MergeTestConfigs(configs), bestStandard, "combined");
            }
            return NewResultSet(configs, best, types.Contains("dpi") ? "dpi" : "standard");
        }

        public static string GetResultScore(string name)
        {
            if (Ctx.LatestResults == null) return null;
            var c = Ctx.LatestResults.Configs.FirstOrDefault(x => x.Name == name);
            return c != null ? $"в последнем тесте {c.Ok} из {c.Total}" : null;
        }
    }
}
