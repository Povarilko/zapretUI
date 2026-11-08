using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace ZapretUi
{
    public class ReleaseAsset
    {
        public string Version;
        public string Url;
        public long Size;
        public string Name;
    }

    // Проверка обновлений и автообновление zapret с GitHub Flowseal
    public static class Updater
    {
        public const string VersionUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt";
        public const string DownloadUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/latest";
        public const string ReleaseApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";

        public static System.Threading.Tasks.Task<string> CheckTask = null;

        // Тот же источник, что у service.bat check_updates. Запрос асинхронный, ответ забирает таймер окна.
        public static void StartUpdateCheck()
        {
            if (!File.Exists(Ctx.UpdFlag)) return;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var wc = new WebClient();
                wc.Headers["Cache-Control"] = "no-cache";
                CheckTask = wc.DownloadStringTaskAsync(VersionUrl);
            }
            catch { }
        }

        public static bool UpdateCheckCompleted()
        {
            var t = CheckTask;
            if (t == null || !t.IsCompleted) return false;
            CheckTask = null;
            if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion) return false;
            string remote = t.Result.Trim();
            string local = Zapret.GetLocalVersion();
            if (remote.Length == 0 || remote == local) return false;
            Ctx.NewVersion = remote;
            return true;
        }

        // Папка, рядом с которой ставить новую версию. Архив часто распаковывают как ...\zapret-X\zapret-X —
        // тогда новую кладём рядом с внешней папкой, а не внутрь неё
        public static string GetUpdateBaseFolder()
        {
            string parent = Directory.GetParent(Ctx.Root).FullName;
            if (string.Equals(Directory.GetParent(parent)?.Name, Path.GetFileName(Ctx.Root), StringComparison.OrdinalIgnoreCase))
                parent = Directory.GetParent(parent).FullName;
            return parent;
        }

        public static ReleaseAsset GetReleaseAsset(string version)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try
            {
                var wc = new WebClient();
                wc.Headers["User-Agent"] = "zapret-ui";
                string json = wc.DownloadString(ReleaseApi);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var tg) ? tg.GetString() : version;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var an) ? an.GetString() : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ReleaseAsset
                            {
                                Version = tag,
                                Url = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null,
                                Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                                Name = name,
                            };
                        }
                    }
                }
            }
            catch { }
            // API недоступно (лимит запросов) — адрес по образцу релизов Flowseal
            string fallback = "zapret-discord-youtube-" + version + ".zip";
            return new ReleaseAsset
            {
                Version = version,
                Url = $"https://github.com/Flowseal/zapret-discord-youtube/releases/download/{version}/{fallback}",
                Size = 0,
                Name = fallback,
            };
        }

        // Куда ставить: в папку окна, если в ней больше ничего нет; иначе — в подпапку zapret
        public static string GetDefaultInstallFolder()
        {
            var own = new[] { "zapret-ui.exe", "zapret-ui.ps1", "utils" };
            bool other;
            try
            {
                other = Directory.GetFileSystemEntries(Ctx.Root).Any(e => !own.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase));
            }
            catch { other = false; }
            return other ? Path.Combine(Ctx.Root, "zapret") : Ctx.Root;
        }

        // Переносит окно в папку с zapret и запускает его оттуда. Здешние копии удаляются
        public static void OpenZapretFolder(string dir)
        {
            bool moved = !Zapret.TestSameFolder(dir, Ctx.Root);
            if (moved)
            {
                // Переезд: копируем загрузчик (он сам при первом запуске распакует приложение)
                File.Copy(Ctx.DistributeExe, Path.Combine(dir, "zapret-ui.exe"), true);
            }
            Process2.StartHidden(dir, Path.Combine(dir, "zapret-ui.exe"), "");
            if (moved)
            {
                try { File.Delete(Path.Combine(Ctx.Root, "zapret-ui.exe")); } catch { }
            }
        }
    }

    public static class Process2
    {
        public static void StartHidden(string workingDir, string file, string args)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = true,
            });
        }
    }
}
