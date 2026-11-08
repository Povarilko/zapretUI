using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ZapretUi
{
    public class ZapretState
    {
        public bool Running;
        public bool ServiceExists;
        public string ServiceStatus;
        public string ServiceStrategy;
        public string DivertStatus;
        public string Mode;   // none | service | manual | unknown
        public string Active;
        public bool Testing;
    }

    // Стратегия: файл .bat и имя без расширения — именно так оно everywhere в UI, истории и результатах
    public class Strategy
    {
        public FileInfo File;
        public string Name; // basename, без .bat
    }

    public class GameFilterInfo
    {
        public string Mode;   // off | all | tcp | udp
        public string TCP;
        public string UDP;
        public string Any;
        public string TcpRange;
        public string UdpRange;
    }

    public static class Zapret
    {
        // ---------- strategies ----------

        public static Strategy[] GetStrategies()
        {
            if (!Directory.Exists(Ctx.Root)) return Array.Empty<Strategy>();
            return Directory.GetFiles(Ctx.Root, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileNameWithoutExtension(f).Equals(Ctx.BaselineName, StringComparison.OrdinalIgnoreCase))
                .Select(f => new Strategy { File = new FileInfo(f), Name = Path.GetFileNameWithoutExtension(f) })
                .OrderBy(s => Str.NaturalKey(s.Name), StringComparer.Ordinal)
                .ToArray();
        }

        public static void NewBaselineBat()
        {
            Fs.WriteTextFile(Ctx.BaselineBat, new[]
            {
                "@echo off",
                ":: Created by zapret-ui for the \"no bypass\" test run and removed after it.",
                ":: winws watches only port 1, so traffic goes as without zapret.",
                "start \"zapret: %~n0\" /min \"%~dp0bin\\winws.exe\" --wf-tcp=1 --wf-udp=1",
            });
        }

        public static void RemoveBaselineBat()
        {
            try { if (File.Exists(Ctx.BaselineBat)) File.Delete(Ctx.BaselineBat); } catch { }
        }

        // Аргументы winws.exe из .bat: строка от winws.exe" до конца (с продолжениями через ^)
        public static string GetBatArgs(string path)
        {
            var sb = new StringBuilder();
            bool capture = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw;
                if (!capture)
                {
                    int i = line.IndexOf("winws.exe\"", StringComparison.Ordinal);
                    if (i < 0) continue;
                    capture = true;
                    line = line.Substring(i + 10);
                }
                string t = line.TrimEnd();
                bool cont = t.EndsWith("^", StringComparison.Ordinal);
                if (cont) t = t.Substring(0, t.Length - 1);
                sb.Append(' ').Append(t);
                if (!cont) break;
            }
            // Как cmd: ^x вне кавычек превращается в x
            return Regex.Replace(sb.ToString(), "\\^(.)", "$1");
        }

        public static string FormatArgs(string s) => Regex.Replace(s.Replace("\"", "").ToLowerInvariant(), "\\s+", " ").Trim();

        // Значения переменных игрового фильтра — так же, как их выставляет service.bat load_game_filter
        public static GameFilterInfo GameFilterVars(string mode)
        {
            const string all = "1024-65535";
            switch (mode)
            {
                case "all": return new GameFilterInfo { Mode = mode, TCP = all, UDP = all, Any = all };
                case "tcp": return new GameFilterInfo { Mode = mode, TCP = all, UDP = "12", Any = all };
                case "udp": return new GameFilterInfo { Mode = mode, TCP = "12", UDP = all, Any = all };
                default: return new GameFilterInfo { Mode = "off", TCP = "12", UDP = "12", Any = "12" };
            }
        }

        static string ReplaceIgnoreCase(string input, string pattern, string replacement)
            => Regex.Replace(input, Regex.Escape(pattern), m => replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);

        public static string ExpandBatArgs(string path, GameFilterInfo gameVars)
        {
            var s = GetBatArgs(path);
            s = ReplaceIgnoreCase(s, "%BIN%", Ctx.BinDir + "\\");
            s = ReplaceIgnoreCase(s, "%LISTS%", Ctx.ListsDir + "\\");
            s = ReplaceIgnoreCase(s, "%GameFilterTCP%", gameVars.TCP);
            s = ReplaceIgnoreCase(s, "%GameFilterUDP%", gameVars.UDP);
            s = ReplaceIgnoreCase(s, "%GameFilter%", gameVars.Any);
            return s;
        }

        public static string GetStrategyArgs(Strategy s)
        {
            string argLine = ExpandBatArgs(s.File.FullName, GetServiceGameFilter()).Trim();
            var m = Regex.Match(argLine, "%\\w+%");
            if (m.Success) throw new Exception($"В {s.Name} есть переменная {m.Value}, которую окно не знает. Запустите этот .bat вручную или через service.bat.");
            if (string.IsNullOrEmpty(argLine)) throw new Exception($"В {s.Name} не найдена строка запуска winws.exe.");
            return argLine;
        }

        // ---------- winws / netsh / user lists ----------

        public static bool TestTcpTimestamps()
        {
            var outp = Proc.Capture("netsh.exe", "interface tcp show global") ?? "";
            return Regex.IsMatch(outp, "(?im)(timestamps|RFC 1323).*enabled");
        }

        public static void EnableTcpTimestamps()
        {
            if (TestTcpTimestamps()) return;
            Proc.RunHidden("netsh.exe", "interface tcp set global timestamps=enabled", null);
        }

        // service.bat load_user_lists: создать пустые пользовательские списки, если их нет
        public static void InitializeUserLists()
        {
            var defaults = new Dictionary<string, string[]>
            {
                ["ipset-exclude-user.txt"] = new[] { Ctx.IpPlaceholder },
                ["list-general-user.txt"] = new[] { "# Never leave this file empty", "domain.example.abc" },
                ["list-exclude-user.txt"] = new[] { "domain.example.abc" },
            };
            foreach (var kv in defaults)
            {
                string path = Path.Combine(Ctx.ListsDir, kv.Key);
                if (!File.Exists(path)) Fs.WriteTextFile(path, kv.Value);
            }
        }

        // ---------- state ----------

        public static string FindStrategyByCommandLine(string cmdLine)
        {
            int i = cmdLine.ToLowerInvariant().IndexOf("winws.exe", StringComparison.Ordinal);
            if (i < 0) return null;
            string actual = FormatArgs(cmdLine.Substring(i + 9));
            // Текущие порты игрового фильтра и стандартные варианты на случай, если фильтр меняли после запуска
            var variants = new List<GameFilterInfo> { GetServiceGameFilter() };
            foreach (var m in new[] { "off", "all", "tcp", "udp" }) variants.Add(GameFilterVars(m));
            foreach (var s in Ctx.Strategies)
                foreach (var v in variants)
                    if (FormatArgs(ExpandBatArgs(s.File.FullName, v)) == actual)
                        return s.Name;
            return null;
        }

        public static ZapretState GetState()
        {
            var procs = Process.GetProcessesByName("winws");
            bool svcExists = ServiceInstalled();
            string svcStatus = svcExists ? Proc.ServiceStatus("zapret") : null;
            string divStatus = Proc.ServiceStatus("WinDivert");
            string svcStrategy = null;
            if (svcExists)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(Ctx.SvcKey);
                    svcStrategy = k?.GetValue("zapret-discord-youtube") as string;
                }
                catch { }
            }

            string pids = string.Join(",", procs.Select(p => p.Id).OrderBy(x => x));
            if (pids != Ctx.LastPids)
            {
                Ctx.LastPids = pids;
                Ctx.LastDetected = null;
                foreach (var p in procs)
                {
                    var m = Regex.Match(p.MainWindowTitle ?? "", "^zapret: (.+)$");
                    if (m.Success) { Ctx.LastDetected = m.Groups[1].Value; break; }
                }
                if (Ctx.LastDetected == null && procs.Length > 0)
                {
                    foreach (var p in procs)
                    {
                        string cmd = Proc.GetCommandLine(p.Id);
                        if (!string.IsNullOrEmpty(cmd))
                        {
                            string name = FindStrategyByCommandLine(cmd);
                            if (name != null) { Ctx.LastDetected = name; break; }
                        }
                    }
                }
            }

            // Тест перебирает все стратегии подряд: пока он идёт, историю не пишем
            bool testing = false;
            foreach (var name in new[] { "powershell", "pwsh" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    string cmd = Proc.GetCommandLine(p.Id);
                    if (!string.IsNullOrEmpty(cmd) && cmd.Contains("test zapret.ps1")) { testing = true; break; }
                }

            string mode = "none"; string active = null;
            if (svcExists && svcStatus == "Running") { mode = "service"; active = svcStrategy; }
            else if (procs.Length > 0)
            {
                if (Ctx.LastDetected != null) { mode = "manual"; active = Ctx.LastDetected; }
                else mode = "unknown";
            }

            return new ZapretState
            {
                Running = procs.Length > 0,
                ServiceExists = svcExists,
                ServiceStatus = svcStatus,
                ServiceStrategy = svcStrategy,
                DivertStatus = divStatus,
                Mode = mode,
                Active = active,
                Testing = testing,
            };
        }

        // ---------- service ----------

        // Ключ в реестре пропадает только когда служба удалена до конца
        public static bool ServiceInstalled() => Proc.ServiceExists("zapret");

        public static void RemoveServiceForTest()
        {
            string imagePath = null, strategy = null, description = null;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(Ctx.SvcKey);
                if (k != null)
                {
                    imagePath = k.GetValue("ImagePath") as string;
                    strategy = k.GetValue("zapret-discord-youtube") as string;
                    description = k.GetValue("Description") as string;
                }
            }
            catch { }
            if (Ctx.UiState.PendingServiceRestore == null)
            {
                Ctx.UiState.PendingServiceRestore = new PendingRestore { ImagePath = imagePath, Strategy = strategy, Description = description };
                Ctx.UiState.Save();
            }
            Ctx.GraceUntil = DateTime.Now.AddSeconds(30);
            RemoveZapretServiceCore();
        }

        // То же, что «Remove Services» в service.bat для службы zapret
        public static void RemoveZapretServiceCore()
        {
            if (Ctx.GraceUntil < DateTime.Now.AddSeconds(8)) Ctx.GraceUntil = DateTime.Now.AddSeconds(8);
            Proc.Sc("stop zapret");
            Proc.Sc("delete zapret");
            Proc.KillByName("winws");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < 20 && ServiceInstalled(); i++) Thread.Sleep(250);
            if (ServiceInstalled()) throw new Exception("Служба zapret ещё удаляется. Подождите несколько секунд и попробуйте снова.");
        }

        // «Remove Services» в service.bat: служба zapret, winws.exe и драйвер WinDivert
        public static void RemoveAllServicesCore()
        {
            if (ServiceInstalled()) RemoveZapretServiceCore();
            else Proc.KillByName("winws");
            // После остановки winws драйвер WinDivert обычно выгружается и удаляется сам
            Thread.Sleep(300);
            foreach (var n in new[] { "WinDivert", "WinDivert14" })
            {
                if (!Proc.ServiceExists(n)) continue;
                Proc.Sc("stop " + n);
                Proc.Sc("delete " + n);
            }
            Ctx.GraceUntil = DateTime.Now.AddSeconds(3);
        }

        // То же, что «Install Service» в service.bat: служба с аргументами выбранного .bat
        public static void InstallZapretServiceCore(string name)
        {
            var bat = Ctx.Strategies.FirstOrDefault(b => b.Name == name);
            if (bat == null) throw new Exception($"Не найден файл стратегии: {name}.bat");
            string argLine = GetStrategyArgs(bat);

            // Стратегии ссылаются на lists\*-user.txt; в свежей папке их нет, а без них winws сразу завершается
            InitializeUserLists();
            if (ServiceInstalled()) RemoveZapretServiceCore();
            Proc.KillByName("winws");
            string winws = Path.Combine(Ctx.BinDir, "winws.exe");
            // Как New-Service: ImagePath = "winws" аргументы
            Proc.Sc($"create zapret binPath= \"\\\"{winws}\\\" {argLine}\" start= auto DisplayName= \"zapret\"");
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(Ctx.SvcKey);
                k?.SetValue("zapret-discord-youtube", name);
            }
            catch { }
            Proc.Sc("description zapret \"Zapret DPI bypass software\"");
            EnableTcpTimestamps();
            Proc.Sc("start zapret");
            Ctx.GraceUntil = DateTime.Now.AddSeconds(8);
            Ctx.UiState.SetLastStrategy(name);

            // Служба может стартовать и тут же упасть — проверяем
            Thread.Sleep(1500);
            string status = Proc.ServiceStatus("zapret");
            if (status != "Running" || Process.GetProcessesByName("winws").Length == 0)
            {
                var missing = Regex.Matches(argLine, "\"([A-Za-z]:\\\\[^\"]+)\"")
                    .Select(m => m.Groups[1].Value).Distinct().Where(p => !File.Exists(p)).ToArray();
                string why = missing.Length > 0
                    ? "Не найдены файлы:\n" + string.Join("\n", missing)
                    : "Проверьте, не мешает ли антивирус или другой обход (диагностика в service.bat).";
                throw new Exception($"Служба zapret установлена со стратегией {name}, но сразу остановилась.\n\n{why}");
            }
        }

        public static void RestoreServiceAfterTestCore()
        {
            var p = Ctx.UiState.PendingServiceRestore;
            if (p == null) return;
            Ctx.GraceUntil = DateTime.Now.AddSeconds(15);
            if (!ServiceInstalled())
            {
                Proc.KillByName("winws");
                string desc = !string.IsNullOrEmpty(p.Description) ? p.Description : "Zapret DPI bypass software";
                Proc.Sc($"create zapret binPath= \"{p.ImagePath}\" start= auto DisplayName= \"zapret\"");
                Proc.Sc($"description zapret \"{desc}\"");
                if (!string.IsNullOrEmpty(p.Strategy))
                {
                    try
                    {
                        using var k = Registry.LocalMachine.CreateSubKey(Ctx.SvcKey);
                        k?.SetValue("zapret-discord-youtube", p.Strategy);
                    }
                    catch { }
                }
                Proc.Sc("start zapret");
            }
            Ctx.UiState.PendingServiceRestore = null;
            Ctx.UiState.Save();
        }

        // Запуск стратегии без службы (через winws.exe напрямую, без консоли)
        public static void StartStrategyManualCore(string name)
        {
            var bat = Ctx.Strategies.FirstOrDefault(b => b.Name == name);
            if (bat == null) throw new Exception($"Не найден файл стратегии: {name}.bat");
            string argLine = GetStrategyArgs(bat);
            Proc.KillByName("winws");
            Thread.Sleep(300);

            InitializeUserLists();
            EnableTcpTimestamps();
            var psi = new ProcessStartInfo(Path.Combine(Ctx.BinDir, "winws.exe"), argLine)
            {
                WorkingDirectory = Ctx.BinDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            Thread.Sleep(700);
            if (p == null || p.HasExited)
                throw new Exception($"winws.exe сразу завершился (код {(p?.ExitCode ?? -1)}). Попробуйте запустить {name}.bat вручную, чтобы увидеть ошибку.");
            Ctx.LastPids = "-";
            Ctx.PendingSource = "ui";
            Ctx.GraceUntil = DateTime.Now.AddSeconds(8);
            Ctx.UiState.SetLastStrategy(name);
        }

        // ---------- game filter ----------

        static GameFilterInfo _gfCache;
        static DateTime _gfCacheAt = DateTime.MinValue;

        public static bool TestGameFilterRanges()
        {
            try { return File.Exists(Ctx.ServiceBat) && File.ReadAllText(Ctx.ServiceBat).Contains("GameFilterTCPRange"); }
            catch { return false; }
        }

        // Разбор файла игрового фильтра любой версии: Mode off|all|tcp|udp и диапазоны (если заданы)
        public static (string Mode, string Tcp, string Udp) ReadGameFilterFile(string path)
        {
            var result = (Mode: "off", Tcp: (string)null, Udp: (string)null);
            if (!File.Exists(path)) return result;
            var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Any(l => l.Contains('=')))
            {
                foreach (var l in lines)
                {
                    int idx = l.IndexOf('=');
                    if (idx < 0) continue;
                    string k = l.Substring(0, idx).Trim().ToLowerInvariant();
                    string v = l.Substring(idx + 1).Trim();
                    switch (k)
                    {
                        case "mode":
                            result.Mode = (v == "all" || v == "tcp" || v == "udp") ? v : "off";
                            break;
                        case "tcp": if (v.Length > 0) result.Tcp = v; else result.Mode = "tcp"; break;
                        case "udp": if (v.Length > 0) result.Udp = v; else result.Mode = "udp"; break;
                        case "all": result.Mode = "all"; break;
                    }
                }
            }
            else
            {
                string first = lines.Length > 0 ? lines[0].ToLowerInvariant() : "";
                result.Mode = first == "all" ? "all" : first == "tcp" ? "tcp" : "udp";
            }
            return result;
        }

        // Значения %GameFilter%, %GameFilterTCP%, %GameFilterUDP% — ровно как их выставит service.bat этой версии
        public static GameFilterInfo GetServiceGameFilter()
        {
            if (_gfCache != null && (DateTime.Now - _gfCacheAt).TotalSeconds < 3) return _gfCache;
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string cmdLine = "/d /s /c \"call \"" + Ctx.ServiceBat + "\" load_game_filter >nul 2>&1 & set GameFilter\"";
                var psi = new ProcessStartInfo("cmd.exe", cmdLine)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Ctx.Root,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (Match m in Regex.Matches(outp, "(?m)^(GameFilter\\w*)=(.*?)\\r?$"))
                        vars[m.Groups[1].Value] = m.Groups[2].Value;
                }
            }
            catch { }
            GameFilterInfo g;
            if (vars.ContainsKey("GameFilterTCP"))
            {
                string st = vars.TryGetValue("GameFilterStatus", out var s) ? s : null;
                string mode = "off";
                if (st != null)
                {
                    if (Regex.IsMatch(st, "TCP and UDP")) mode = "all";
                    else if (Regex.IsMatch(st, "\\(TCP\\)")) mode = "tcp";
                    else if (Regex.IsMatch(st, "\\(UDP\\)")) mode = "udp";
                }
                g = new GameFilterInfo
                {
                    Mode = mode,
                    TCP = vars["GameFilterTCP"],
                    UDP = vars.TryGetValue("GameFilterUDP", out var u) ? u : null,
                    Any = vars.TryGetValue("GameFilter", out var a) ? a : null,
                    TcpRange = vars.TryGetValue("GameFilterTCPRange", out var tr) ? tr : null,
                    UdpRange = vars.TryGetValue("GameFilterUDPRange", out var ur) ? ur : null,
                };
            }
            else
            {
                // service.bat не ответил — считаем сами по файлу, как старые версии
                var f = ReadGameFilterFile(Ctx.GameFlag);
                g = GameFilterVars(f.Mode);
            }
            _gfCache = g;
            _gfCacheAt = DateTime.Now;
            return g;
        }

        // Проверка портов как в service.bat: «1024-65535», «27015», через запятую, без ведущих нулей
        public static string TestPortRange(string text)
        {
            string v = Regex.Replace(text ?? "", "\\s", "");
            if (v.Length == 0) return null;
            foreach (var item in v.Split(','))
            {
                var m = Regex.Match(item, "^([1-9]\\d{0,4})(?:-([1-9]\\d{0,4}))?$");
                if (!m.Success) return null;
                int a = int.Parse(m.Groups[1].Value);
                int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
                if (a > 65535 || b > 65535 || a > b) return null;
            }
            return v;
        }

        public static void SetGameFilter(string mode, string tcpRange = null, string udpRange = null)
        {
            if (TestGameFilterRanges())
            {
                var cur = ReadGameFilterFile(Ctx.GameFlag);
                string tcp = tcpRange ?? cur.Tcp ?? "1024-65535";
                string udp = udpRange ?? cur.Udp ?? "1024-65535";
                string m = mode == "off" ? "disabled" : mode;
                Fs.WriteTextFile(Ctx.GameFlag, new[] { $"mode={m}", $"tcp={tcp}", $"udp={udp}" });
            }
            else if (mode == "off")
            {
                try { if (File.Exists(Ctx.GameFlag)) File.Delete(Ctx.GameFlag); } catch { }
            }
            else Fs.WriteTextFile(Ctx.GameFlag, new[] { mode });
            _gfCache = null;
        }

        // ---------- ipset ----------

        public static string GetIpsetStatus()
        {
            if (!File.Exists(Ctx.IpsetFile)) return "any";
            string text = File.ReadAllText(Ctx.IpsetFile).Trim('\uFEFF', ' ', '\r', '\n', '\t');
            if (text.Length == 0) return "any";
            if (text.Contains(Ctx.IpPlaceholder)) return "none";
            return "loaded";
        }

        // Та же логика, что у service.bat: при уходе из loaded полный список сохраняется в .backup
        public static void SetIpsetStatus(string target)
        {
            string cur = GetIpsetStatus();
            if (cur == target) return;
            if (target == "loaded")
            {
                if (!File.Exists(Ctx.IpsetBak)) throw new Exception("Нет сохранённого списка IP. Нажмите «Обновить список IPset».");
                try { if (File.Exists(Ctx.IpsetFile)) File.Delete(Ctx.IpsetFile); } catch { }
                File.Move(Ctx.IpsetBak, Ctx.IpsetFile);
                return;
            }
            if (cur == "loaded") File.Move(Ctx.IpsetFile, Ctx.IpsetBak, true);
            if (target == "none") Fs.WriteTextFile(Ctx.IpsetFile, new[] { Ctx.IpPlaceholder });
            else File.WriteAllBytes(Ctx.IpsetFile, Array.Empty<byte>());
        }

        public static void UpdateIpsetListCore()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using var wc = new WebClient();
            string content = wc.DownloadString(Ctx.IpsetUrl);
            if (string.IsNullOrEmpty(content)) throw new Exception("Не удалось скачать список IPset.");
            Fs.WriteTextFile(Ctx.IpsetFile, content);
        }

        // ---------- misc ----------

        public static string GetLocalVersion()
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(Ctx.ServiceBat), "LOCAL_VERSION=([\\d.]+)");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return "?";
        }

        // Папка zapret, из которой установлена служба (по пути к winws.exe в ImagePath), или null
        public static string GetServiceRoot()
        {
            string img = null;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(Ctx.SvcKey);
                img = k?.GetValue("ImagePath") as string;
            }
            catch { return null; }
            if (img == null) return null;
            var m = Regex.Match(img, "^\\s*\"?([^\"]+?\\\\)bin\\\\winws\\.exe", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.TrimEnd('\\') : null;
        }

        public static bool TestSameFolder(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool TestZapretFolder(string dir)
            => File.Exists(Path.Combine(dir, "service.bat")) && File.Exists(Path.Combine(dir, "bin", "winws.exe"));

        // Копирует содержимое папки в другую с заменой файлов
        public static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var sub in Directory.GetDirectories(from))
                CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
