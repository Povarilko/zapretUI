using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace ZapretUi
{
    public class AppUpdateInfo
    {
        public Version Version;
        public string Url;
        public string Name;
        public long Size;
        public string PageUrl;
    }

    // Обновление самого zapret UI с релизов GitHub.
    // Работает через загрузчик: новый exe (загрузчик со встроенным payload)
    // заменяет старый в папке zapret, перезапуск — и новая версия распаковывается сама.
    public static class AppUpdate
    {
        const string RepoApi = "https://api.github.com/repos/Povarilko/zapretUI/releases/latest";
        public const string ReleasePage = "https://github.com/Povarilko/zapretUI/releases/latest";

        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        // null — обновлений нет или не удалось проверить
        public static AppUpdateInfo Check()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using var wc = new WebClient();
                wc.Headers["User-Agent"] = "zapret-ui";
                wc.Encoding = System.Text.Encoding.UTF8;
                string json = wc.DownloadString(RepoApi);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var tg) ? tg.GetString() : "";
                if (!Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var remote)) return null;
                if (remote <= CurrentVersion) return null;
                string page = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : ReleasePage;
                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return null;
                // Предпочитаем голый exe; иначе zip с exe внутри
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var an) ? an.GetString() : "";
                    if (!string.Equals(name, "zapret-ui.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    return new AppUpdateInfo
                    {
                        Version = remote,
                        Url = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null,
                        Name = name,
                        Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                        PageUrl = page,
                    };
                }
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var an) ? an.GetString() : "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    return new AppUpdateInfo
                    {
                        Version = remote,
                        Url = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null,
                        Name = name,
                        Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                        PageUrl = page,
                    };
                }
            }
            catch { }
            return null;
        }

        // Скачивает ассет и возвращает путь к готовому exe во временной папке
        public static string DownloadAndPrepare(AppUpdateInfo info)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "zapret-ui-update-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(tmp);
            string downloaded = Path.Combine(tmp, info.Name);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "zapret-ui";
                wc.DownloadFile(info.Url, downloaded);
            }
            if (!File.Exists(downloaded) || new FileInfo(downloaded).Length < 100000)
                throw new Exception("Файл обновления получен слишком маленьким — что-то пошло не так.");
            if (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(downloaded);
                var entry = zip.GetEntry("zapret-ui.exe")
                    ?? throw new Exception("В архиве обновления нет zapret-ui.exe.");
                string exe = Path.Combine(tmp, "zapret-ui.exe");
                entry.ExtractToFile(exe, true);
                return exe;
            }
            return downloaded;
        }

        // Заменяет загрузчик новым и перезапускает приложение из него
        public static void ReplaceAndRestart(string newExe)
        {
            string launcher = Ctx.LauncherPath;
            if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
                throw new Exception("Запущена копия без загрузчика — обновите вручную со страницы релиза.");
            string bak = launcher + ".bak";
            try { File.Copy(launcher, bak, true); } catch { }
            try
            {
                File.Copy(newExe, launcher, true);
            }
            catch
            {
                try { if (File.Exists(bak)) File.Copy(bak, launcher, true); } catch { }
                throw new Exception("Не удалось заменить файл — возможно, он занят. Закройте zapret UI и попробуйте снова.");
            }
            Process.Start(new ProcessStartInfo(launcher, $"-ZapretRoot \"{Ctx.Root}\"")
            {
                WorkingDirectory = Ctx.Root,
                UseShellExecute = true,
            });
            System.Windows.Application.Current.Shutdown();
        }

        public static void OpenReleasePage()
        {
            Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true });
        }
    }
}
