using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ZapretUi
{
    public class DiagItem
    {
        public string Level; // good | mid | bad
        public string Title;
        public string Detail;
        public string Link;

        public DiagItem(string level, string title, string detail = "", string link = "")
        {
            Level = level; Title = title; Detail = detail; Link = link;
        }
    }

    // Пункт «Run Diagnostics» меню service.bat: те же проверки и те же автоисправления
    public static class Diagnostics
    {
        static readonly string[] ConflictNames = { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };
        static readonly (string Dir, string Process, string Name)[] DiscordInstalls =
        {
            ("discord", "Discord", "Discord"),
            ("discordptb", "DiscordPTB", "Discord PTB"),
            ("discordcanary", "DiscordCanary", "Discord Canary"),
            ("discorddevelopment", "DiscordDevelopment", "Discord Development"),
        };

        static bool ServiceExists(string name) => Proc.ServiceExists(name);

        // sc stop + sc delete; true, если служба исчезла
        static bool RemoveServiceByName(string name)
        {
            Proc.Sc("stop " + name);
            Proc.Sc("delete " + name);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < 10 && ServiceExists(name); i++) Thread.Sleep(200);
            return !ServiceExists(name);
        }

        static List<(string Name, string DisplayName)> EnumerateServices()
        {
            var list = new List<(string, string)>();
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (k == null) return list;
                foreach (var sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using var sk = k.OpenSubKey(sub);
                        list.Add((sub, sk?.GetValue("DisplayName") as string ?? ""));
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        // Как «sc query»: только работающие службы, совпадение по имени или отображаемому имени
        static List<(string Name, string DisplayName)> FindActiveServices(string pattern)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            return EnumerateServices()
                .Where(s => regex.IsMatch(s.Name) || regex.IsMatch(s.DisplayName))
                .Where(s =>
                {
                    string st = Proc.ServiceStatus(s.Name);
                    return st != null && st != "Stopped";
                })
                .ToList();
        }

        static bool HasDoh()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
                if (root == null) return false;
                return Scan(root);
                bool Scan(RegistryKey key)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(sub);
                        if (sk == null) continue;
                        var v = sk.GetValue("DohFlags");
                        if (v is int i && i > 0) return true;
                        if (Scan(sk)) return true;
                    }
                    return false;
                }
            }
            catch { }
            return false;
        }

        public static List<DiagItem> GetResults()
        {
            var r = new List<DiagItem>();
            void Add(string level, string title, string detail = "", string link = "") => r.Add(new DiagItem(level, title, detail, link));
            const string issues = "https://github.com/Flowseal/zapret-discord-youtube/issues";
            string moveHint = "Если обход не работает, перенесите папку zapret в другое место, например в C:\\zapret.";

            string bfe = Proc.ServiceStatus("BFE");
            if (bfe == "Running") Add("good", "Служба Base Filtering Engine работает");
            else Add("bad", "Служба Base Filtering Engine не запущена", "Без неё zapret не работает. Запустите её в services.msc и поставьте тип запуска «Автоматически».");

            bool proxyOn = false; string proxyServer = null;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                if (k != null)
                {
                    proxyOn = k.GetValue("ProxyEnable") is int i && i == 1;
                    proxyServer = k.GetValue("ProxyServer") as string;
                }
            }
            catch { }
            if (proxyOn) Add("mid", $"Включён системный прокси: {proxyServer}", "Проверьте, что он рабочий, или выключите его, если не пользуетесь прокси.");
            else Add("good", "Системный прокси выключен");

            if (Zapret.TestTcpTimestamps()) Add("good", "TCP timestamps включены");
            else
            {
                Proc.RunHidden("netsh.exe", "interface tcp set global timestamps=enabled", null);
                if (Zapret.TestTcpTimestamps()) Add("good", "TCP timestamps были выключены — теперь включены");
                else Add("bad", "Не удалось включить TCP timestamps", "Выполните от администратора: netsh interface tcp set global timestamps=enabled");
            }

            if (Process.GetProcessesByName("AdguardSvc").Length > 0) Add("bad", "Запущен Adguard", "Adguard может мешать работе Discord.", issues + "/417");
            else Add("good", "Adguard не найден");

            var killer = FindActiveServices("Killer");
            if (killer.Count > 0) Add("bad", "Найдены службы Killer", "Killer конфликтует с zapret.", issues + "/2512#issuecomment-2821119513");
            else Add("good", "Службы Killer не найдены");

            var intel = FindActiveServices("Intel").Where(s =>
                Regex.IsMatch(s.Name + " " + s.DisplayName, "Connectivity", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(s.Name + " " + s.DisplayName, "Network", RegexOptions.IgnoreCase)).ToList();
            if (intel.Count > 0) Add("bad", "Найдена служба Intel Connectivity Network Service", "Она конфликтует с zapret.", "https://github.com/ValdikSS/GoodbyeDPI/issues/541#issuecomment-2661670982");
            else Add("good", "Intel Connectivity Network Service не найдена");

            if (FindActiveServices("TracSrvWrapper|EPWD").Count > 0) Add("bad", "Найдены службы Check Point", "Check Point конфликтует с zapret. Попробуйте удалить Check Point.");
            else Add("good", "Службы Check Point не найдены");

            if (FindActiveServices("SmartByte").Count > 0) Add("bad", "Найдены службы SmartByte", "SmartByte конфликтует с zapret. Удалите его или отключите в services.msc.");
            else Add("good", "Службы SmartByte не найдены");

            if (Regex.IsMatch(Ctx.Root, "[а-яА-ЯёЁ]")) Add("mid", "В пути к zapret есть кириллица", moveHint);
            else Add("good", "В пути к zapret нет кириллицы");

            string od = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(od) && (Ctx.Root.TrimEnd('\\') + "\\").StartsWith(od.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                Add("bad", "zapret лежит в папке OneDrive", moveHint);
            else Add("good", "zapret не в папке OneDrive");

            if (Directory.Exists(Ctx.BinDir) && Directory.GetFiles(Ctx.BinDir, "*.sys").Length > 0) Add("good", "Драйвер WinDivert64.sys на месте");
            else Add("bad", "Не найден драйвер WinDivert64.sys", "В папке bin нет файла .sys. Распакуйте архив zapret заново; если файл пропадает, его удаляет антивирус.");

            var vpn = FindActiveServices("VPN|HappService");
            if (vpn.Count > 0) Add("mid", "Найдены службы VPN: " + string.Join(", ", vpn.Select(s => s.DisplayName)), "Некоторые VPN конфликтуют с zapret. Выключите все VPN.");
            else Add("good", "Службы VPN не найдены");

            if (HasDoh()) Add("good", "Зашифрованный DNS настроен");
            else Add("mid", "Зашифрованный DNS не настроен", "Включите в браузере безопасный DNS с нестандартным провайдером. В Windows 11 зашифрованный DNS можно включить в Параметрах — тогда это предупреждение пропадёт.");

            try
            {
                string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
                bool yt = File.Exists(hosts) && File.ReadAllLines(hosts)
                    .Any(l => !Regex.IsMatch(l, "^\\s*#") && Regex.IsMatch(l, "youtube\\.com|youtu\\.be"));
                if (yt) Add("mid", "В файле hosts есть записи для youtube.com или youtu.be", "Они могут мешать доступу к YouTube.");
                else Add("good", "В файле hosts нет записей YouTube");
            }
            catch { }

            // WinDivert без winws.exe остаётся от другого обхода или от упавшего zapret
            string div = Proc.ServiceStatus("WinDivert");
            if (Process.GetProcessesByName("winws").Length == 0 && (div == "Running" || div == "StopPending"))
            {
                if (RemoveServiceByName("WinDivert")) Add("good", "WinDivert работал без winws.exe — удалён");
                else Add("bad", "WinDivert работает без winws.exe и не удаляется", "Его держит другой обход. Удалите службы других обходов ниже или найдите, какая программа использует WinDivert.");
            }

            Ctx.DiagConflicts = ConflictNames.Where(ServiceExists).ToList();
            if (Ctx.DiagConflicts.Count > 0) Add("bad", "Найдены службы других обходов: " + string.Join(", ", Ctx.DiagConflicts), "Они мешают работе zapret. Их можно удалить кнопкой ниже.");
            else Add("good", "Других обходов не найдено");

            return r;
        }

        // Службы других обходов и драйвер WinDivert
        public static List<DiagItem> RemoveConflicts(IEnumerable<string> names)
        {
            var r = new List<DiagItem>();
            foreach (var n in names)
            {
                if (RemoveServiceByName(n)) r.Add(new DiagItem("good", $"Служба {n} удалена"));
                else r.Add(new DiagItem("bad", $"Не удалось удалить службу {n}", "Попробуйте после перезагрузки или удалите программу этого обхода."));
            }
            foreach (var n in new[] { "WinDivert", "WinDivert14" })
                if (ServiceExists(n)) RemoveServiceByName(n);
            return r;
        }

        static List<(string Dir, string Process, string Name)> GetDiscordInstalls()
        {
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var list = new List<(string, string, string)>();
            if (appdata.Length == 0) return list;
            foreach (var d in DiscordInstalls)
                if (Directory.Exists(Path.Combine(appdata, d.Dir))) list.Add(d);
            return list;
        }

        // :clear_discord_cache из service.bat: закрыть Discord и удалить Cache, Code Cache, GPUCache
        public static List<DiagItem> ClearDiscordCache()
        {
            var r = new List<DiagItem>();
            var found = GetDiscordInstalls();
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (found.Count == 0)
            {
                r.Add(new DiagItem("bad", "Discord не найден", $"Нет ни одной папки Discord в {appdata}."));
                return r;
            }
            foreach (var d in found)
            {
                string dir = Path.Combine(appdata, d.Dir);
                var procs = Process.GetProcessesByName(d.Process);
                string note = "";
                if (procs.Length > 0)
                {
                    foreach (var p in procs) { try { Proc.Kill(p.Id); } catch { } }
                    foreach (var p in procs) { try { p.WaitForExit(3000); } catch { } }
                    note = Process.GetProcessesByName(d.Process).Length > 0
                        ? $"Не удалось закрыть {d.Name}."
                        : $"{d.Name} был закрыт — запустите его снова.";
                }
                var deleted = new List<string>();
                var failed = new List<string>();
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    string path = Path.Combine(dir, sub);
                    if (!Directory.Exists(path)) continue;
                    // Занятые файлы остаются, остальное удаляется — как rd /s /q
                    try { Directory.Delete(path, true); } catch { }
                    if (Directory.Exists(path)) failed.Add(sub); else deleted.Add(sub);
                }
                if (failed.Count > 0)
                    r.Add(new DiagItem("bad", $"{d.Name}: не удалось удалить {string.Join(", ", failed)}",
                        ($"Файлы заняты. Закройте {d.Name} полностью, в том числе в трее, и попробуйте снова. " + note).Trim()));
                else if (deleted.Count > 0)
                    r.Add(new DiagItem("good", $"{d.Name}: кэш очищен", ($"Удалено: {string.Join(", ", deleted)}. " + note).Trim()));
                else
                    r.Add(new DiagItem("good", $"{d.Name}: кэш уже пуст", note));
            }
            return r;
        }

        public static int DiscordInstallCount => GetDiscordInstalls().Count;

        public static string DiscordInstallNames => string.Join(", ", GetDiscordInstalls().Select(d => d.Name));
    }
}
