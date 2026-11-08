using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZapretUi
{
    public class TargetItem
    {
        public string Key;
        public string Value;
    }

    public class TargetGroup
    {
        public string Name;
        public List<TargetItem> Items = new List<TargetItem>();
        public string Text; // текст редактора (адреса по строкам)
    }

    public class TargetsModel
    {
        public List<string> Header = new List<string>();
        public List<TargetGroup> Groups = new List<TargetGroup>();
    }

    public class TargetsParseResult
    {
        public List<string> Values = new List<string>();
        public List<string> Errors = new List<string>();
        public List<string> Duplicates = new List<string>();
    }

    // utils\targets.txt: комментарии в начале, затем блоки «### Группа» и строки «Ключ = "https://..."».
    public static class Targets
    {
        public static Dictionary<string, string> GetTargetGroups()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string group = "Другое";
            if (!File.Exists(Ctx.TargetsTxt)) return map;
            foreach (var line in File.ReadAllLines(Ctx.TargetsTxt))
            {
                var g = Regex.Match(line, "^###\\s*(.+?)\\s*$");
                if (g.Success) { group = g.Groups[1].Value; continue; }
                var k = Regex.Match(line, "^\\s*(\\w+)\\s*=");
                if (k.Success) map[k.Groups[1].Value] = group;
            }
            return map;
        }

        public static TargetsModel ReadTargetsModel(string path)
        {
            var model = new TargetsModel();
            TargetGroup cur = null;
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var g = Regex.Match(line, "^###\\s*(.+?)\\s*$");
                    if (g.Success)
                    {
                        cur = new TargetGroup { Name = g.Groups[1].Value };
                        model.Groups.Add(cur);
                        continue;
                    }
                    var k = Regex.Match(line, "^\\s*(\\w+)\\s*=\\s*\"(.+)\"\\s*$");
                    if (k.Success)
                    {
                        if (cur == null) { cur = new TargetGroup { Name = "Другое" }; model.Groups.Add(cur); }
                        cur.Items.Add(new TargetItem { Key = k.Groups[1].Value, Value = k.Groups[2].Value });
                        continue;
                    }
                    if (cur == null && line.StartsWith("#", StringComparison.Ordinal)) model.Header.Add(line);
                }
            }
            return model;
        }

        // Строка из формы -> значение для targets.txt. null, если строку не удалось понять
        public static string ConvertToTargetValue(string text)
        {
            string t = (text ?? "").Trim();
            var m = Regex.Match(t, "^(?i)ping:\\s*(\\S+)$");
            if (m.Success) return "PING:" + m.Groups[1].Value;
            if (Regex.IsMatch(t, "^\\d{1,3}(\\.\\d{1,3}){3}$")) return "PING:" + t;
            if (Regex.IsMatch(t, "^(?i)https?://\\S+$")) return t;
            if (Regex.IsMatch(t, "^(?i)[a-z0-9-]+(\\.[a-z0-9-]+)+(/\\S*)?$")) return "https://" + t;
            return null;
        }

        // Значение из targets.txt -> как его показать в форме
        public static string FormatTargetValue(string value)
        {
            var pm = Regex.Match(value ?? "", "^PING:(.+)$");
            if (pm.Success) return pm.Groups[1].Value;
            var m = Regex.Match(value ?? "", "^https://([^/]+)/?$");
            if (m.Success) return m.Groups[1].Value;
            return value;
        }

        // Значение -> строка редактора. Пинг по имени хоста помечаем «ping:»
        public static string FormatTargetLine(string value)
        {
            var pm = Regex.Match(value ?? "", "^PING:(.+)$");
            if (pm.Success && !Regex.IsMatch(pm.Groups[1].Value, "^\\d{1,3}(\\.\\d{1,3}){3}$"))
                return "ping:" + pm.Groups[1].Value;
            return FormatTargetValue(value);
        }

        // Текст редактора группы -> адреса. В строке может быть несколько адресов через запятую или пробел
        public static TargetsParseResult ReadTargetsText(string text)
        {
            var result = new TargetsParseResult();
            var lines = Regex.Split(text ?? "", "\r?\n");
            for (int n = 0; n < lines.Length; n++)
            {
                string prepared = Regex.Replace(lines[n], "(?i)\\bping:\\s+", "PING:");
                foreach (var tok in Regex.Split(prepared, "[\\s,;]+").Where(x => x.Length > 0))
                {
                    string val = ConvertToTargetValue(tok);
                    if (val == null) { result.Errors.Add($"строка {n + 1}: «{tok}»"); continue; }
                    if (result.Values.Contains(val)) { result.Duplicates.Add(FormatTargetLine(val)); continue; }
                    result.Values.Add(val);
                }
            }
            return result;
        }

        public static string GetAsciiWord(string s)
        {
            var parts = Regex.Split(s ?? "", "[^A-Za-z0-9]+").Where(x => x.Length > 0)
                .Select(x => char.ToUpperInvariant(x[0]) + x.Substring(1));
            return string.Concat(parts);
        }

        // Ключ для нового адреса: группа + имя хоста латиницей, например SteamStoreSteampowered
        public static string NewTargetKey(string group, string value, HashSet<string> used)
        {
            string g = GetAsciiWord(group);
            if (g.Length == 0) g = "Target";
            string part;
            var pm = Regex.Match(value ?? "", "^PING:(.+)$");
            if (pm.Success) part = "Ping" + Regex.Replace(pm.Groups[1].Value, "[^A-Za-z0-9]", "");
            else
            {
                var hm = Regex.Match(value ?? "", "^(?i)https?://([^/:]+)");
                string hostName = Regex.Replace(hm.Success ? hm.Groups[1].Value : "", "^www\\.", "");
                var labels = hostName.Split('.');
                if (labels.Length > 1) labels = labels.Take(labels.Length - 1).ToArray();
                part = GetAsciiWord(string.Join(" ", labels));
            }
            string baseKey = g + part;
            if (Regex.IsMatch(baseKey, "^\\d")) baseKey = "T" + baseKey;
            string key = baseKey; int n = 2;
            while (used.Contains(key)) { key = baseKey + n; n++; }
            used.Add(key);
            return key;
        }

        public static void SaveTargetsModel(TargetsModel model, string path)
        {
            if (!File.Exists(Ctx.TargetsDefault) && File.Exists(path))
                File.Copy(path, Ctx.TargetsDefault);
            Fs.WriteTextFile(path, ConvertToTargetsLines(model).ToArray());
        }

        // Модель -> строки в формате targets.txt (ключи новым адресам придумываются здесь)
        public static List<string> ConvertToTargetsLines(TargetsModel model)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in model.Groups)
                foreach (var it in g.Items)
                    if (!string.IsNullOrEmpty(it.Key)) used.Add(it.Key);
            foreach (var g in model.Groups)
                foreach (var it in g.Items)
                    if (string.IsNullOrEmpty(it.Key)) it.Key = NewTargetKey(g.Name, it.Value, used);

            int width = 22;
            foreach (var g in model.Groups)
                foreach (var it in g.Items)
                    if (it.Key.Length + 2 > width) width = it.Key.Length + 2;
            var lines = new List<string>();
            if (model.Header.Count > 0) lines.AddRange(model.Header);
            else lines.Add("# targets.txt - endpoint list for zapret.ps1 tests");
            foreach (var g in model.Groups)
            {
                lines.Add("");
                lines.Add("### " + g.Name);
                foreach (var it in g.Items)
                    lines.Add(it.Key.PadRight(width) + "= \"" + it.Value + "\"");
            }
            return lines;
        }

        // Текст группы для редактора: адреса по строкам
        public static string LoadGroupText(TargetGroup g)
            => string.Join("\r\n", g.Items.Select(i => FormatTargetLine(i.Value)));
    }
}
