using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;

namespace ZapretUi
{
    public class TgRelease
    {
        public string Version;
        public string Name;
        public string Error;
        public string Url;
        public long Size;
    }

    public class TgConfig
    {
        public string Secret;
        public string Host;
        public int? Port;
    }

    public class TgStateInfo
    {
        public bool Installed;
        public bool Running;
        public bool Listening;
        public int Port;
        public bool Foreign;
    }

    public class TgDownloadState
    {
        public WebClient Wc;
        public System.Threading.Tasks.Task Task;
        public string Tmp;
        public long Size;
        public string Version;
    }

    // Локальный MTProto-прокси для Telegram Desktop от Flowseal. Окно скачивает его
    // в %LOCALAPPDATA%\Programs\TgWsProxy; настройки и лог — у самого прокси, в %APPDATA%\TgWsProxy.
    public static class TgProxy
    {
        public static readonly string TgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TgWsProxy");
        public static readonly string TgExe = Path.Combine(TgDir, "TgWsProxy.exe");
        public static readonly string TgDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TgWsProxy");
        public const string TgReleaseApi = "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest";
        public const string TgReleasesUrl = "https://github.com/Flowseal/tg-ws-proxy/releases/latest";
        public const string TgRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static TgRelease Latest = null;
        public static System.Threading.Tasks.Task<string> CheckTask = null;
        public static TgDownloadState Download = null;
        public static bool Loading = false;

        public static bool TestInstalled() => File.Exists(TgExe);

        // Файл релиза под эту Windows: ARM64, Windows 7 (32/64) или обычный x64
        public static string GetAssetName()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (string.IsNullOrEmpty(arch)) arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            var os = Environment.OSVersion.Version;
            if (os.Major == 6 && os.Minor <= 1)
                return Environment.Is64BitOperatingSystem ? "TgWsProxy_windows_7_64bit.exe" : "TgWsProxy_windows_7_32bit.exe";
            if (string.Equals(arch, "ARM64", StringComparison.OrdinalIgnoreCase)) return "TgWsProxy_windows_arm64.exe";
            return "TgWsProxy_windows.exe";
        }

        public static string GetVersion()
        {
            if (!TestInstalled()) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(TgExe).FileVersion;
                if (string.IsNullOrEmpty(v)) return "?";
                return Regex2.TrimZeros(v);
            }
            catch { return "?"; }
        }

        // Сравнение версий вида 1.11.4; «v» в начале тега отбрасывается
        public static bool TestNewer(string remote, string local)
        {
            try
            {
                var r = new Version(Regex2.TrimStartV(remote));
                var l = new Version(Regex2.TrimStartV(local));
                return r > l;
            }
            catch { return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase); }
        }

        public static void StartCheck()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers["User-Agent"] = "zapret-ui";
            CheckTask = wc.DownloadStringTaskAsync(TgReleaseApi);
        }

        public static bool UpdateCheck()
        {
            var t = CheckTask;
            if (t == null || !t.IsCompleted) return false;
            CheckTask = null;
            if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                // API недоступен (например, 504 через обход DPI): всё равно даём ссылку —
                // GitHub отдаёт ассет последнего релиза по ней без API, кнопка «Скачать» работает
                string name = GetAssetName();
                Latest = new TgRelease
                {
                    Error = "не удалось узнать последнюю версию",
                    Name = name,
                    Url = $"https://github.com/Flowseal/tg-ws-proxy/releases/latest/download/{name}",
                    Size = 0,
                };
                return true;
            }
            try
            {
                using var doc = JsonDocument.Parse(t.Result);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var tg) ? tg.GetString() : "";
                string name = GetAssetName();
                string url = null;
                long size = 0;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var an) && an.GetString() == name)
                        {
                            url = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                            size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                            break;
                        }
                    }
                }
                Latest = new TgRelease
                {
                    Version = Regex2.TrimStartV(tag),
                    Name = name,
                    Error = null,
                    Url = url ?? $"https://github.com/Flowseal/tg-ws-proxy/releases/download/{tag}/{name}",
                    Size = size,
                };
            }
            catch
            {
                string name = GetAssetName();
                Latest = new TgRelease
                {
                    Error = "не удалось разобрать ответ GitHub",
                    Name = name,
                    Url = $"https://github.com/Flowseal/tg-ws-proxy/releases/latest/download/{name}",
                    Size = 0,
                };
            }
            return true;
        }

        // Процесс прокси — любой TgWsProxy*, в том числе запущенный не из папки окна
        public static List<Process> GetProcesses()
        {
            var list = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                bool ok = false;
                try { ok = p.ProcessName.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase); } catch { }
                if (ok) list.Add(p);
                else try { p.Dispose(); } catch { }
            }
            return list;
        }

        // Настройки прокси (config.json); null, если его ещё ни разу не запускали
        public static TgConfig ReadConfig()
        {
            string f = Path.Combine(TgDataDir, "config.json");
            if (!File.Exists(f)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                var cfg = new TgConfig();
                if (r.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String) cfg.Secret = s.GetString(); // nosec: секрет читается из config.json самого TgWsProxy, в коде его нет
                if (r.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String) cfg.Host = h.GetString();
                if (r.TryGetProperty("port", out var pr) && pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out var pi)) cfg.Port = pi;
                return cfg;
            }
            catch { return null; }
        }

        public static bool TestListening(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            }
            catch { return false; }
        }

        // Та же ссылка, что «Открыть в Telegram» в трее: secret с приставкой dd
        public static string GetLink()
        {
            var c = ReadConfig();
            if (c == null || string.IsNullOrEmpty(c.Secret)) return null;
            string host = !string.IsNullOrEmpty(c.Host) && c.Host != "0.0.0.0" ? c.Host : "127.0.0.1";
            int port = c.Port ?? 1443;
            return $"tg://proxy?server={host}&port={port}&secret=dd{c.Secret}";
        }

        // Запуск от имени пользователя, а не администратора: через проводник
        public static void StartAsUser(string target)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
        }

        public static TgStateInfo GetState()
        {
            bool installed = TestInstalled();
            var procs = GetProcesses();
            var cfg = ReadConfig();
            int port = cfg?.Port ?? 1443;
            string foreign = null;
            foreach (var p in procs)
            {
                try { if (!string.Equals(p.MainModule?.FileName, TgExe, StringComparison.OrdinalIgnoreCase)) foreign = foreign ?? p.ProcessName; } catch { }
            }
            return new TgStateInfo
            {
                Installed = installed,
                Running = procs.Count > 0,
                Listening = procs.Count > 0 && TestListening(port),
                Port = port,
                Foreign = foreign != null,
            };
        }

        // Автозапуск — та же запись, что ставит сам прокси: HKCU\...\Run, «TgWsProxy» = "путь"
        public static bool TestAutostart()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(TgRunKey);
                var v = k?.GetValue("TgWsProxy") as string;
                return v != null && v.Trim() == "\"" + TgExe + "\"";
            }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(TgRunKey);
                    if (on) k.SetValue("TgWsProxy", "\"" + TgExe + "\"");
                    else k.DeleteValue("TgWsProxy", false);
            }
            catch { }
        }

        // Кладёт exe прокси в папку окна. Своя копия, если работает, на время замены останавливается
        public static void InstallFile(string src)
        {
            var vi = FileVersionInfo.GetVersionInfo(src);
            if (vi.ProductName != "TG WS Proxy") throw new Exception($"Это не TG WS Proxy: в свойствах файла указано «{vi.ProductName}».");
            var own = GetProcesses().Where(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, TgExe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
            }).ToList();
            foreach (var p in own) { try { Proc.Kill(p.Id); } catch { } }
            foreach (var p in own) { try { p.WaitForExit(5000); } catch { } }
            Directory.CreateDirectory(TgDir);
            File.Copy(src, TgExe, true);
            if (own.Count > 0) StartAsUser(TgExe);
        }

        public static void StartDownload()
        {
            var l = Latest;
            if (l == null || string.IsNullOrEmpty(l.Url)) throw new Exception("Не удалось узнать, где лежит последняя версия. Проверьте интернет и нажмите «Проверить обновления».");
            Directory.CreateDirectory(TgDir);
            string tmp = Path.Combine(TgDir, "TgWsProxy.download");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers["User-Agent"] = "zapret-ui";
            Download = new TgDownloadState { Wc = wc, Task = wc.DownloadFileTaskAsync(l.Url, tmp), Tmp = tmp, Size = l.Size, Version = l.Version };
        }
    }

    internal static class Regex2
    {
        public static string TrimStartV(string s) => (s ?? "").TrimStart('v', 'V');
        public static string TrimZeros(string v) => System.Text.RegularExpressions.Regex.Replace(v, "(\\.0)+$", "");
    }
}
