using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapretUi
{
    public class MigrItem
    {
        public string Id;
        public string Title;
        public string Detail;
        public bool On;
    }

    public class MigrationResult
    {
        public List<string> Done = new List<string>();
        public string Backup;
    }

    // Переход на новую версию: перенос списков, истории, настроек теста и zapret из старой папки.
    // Экспорт/импорт — тот же перенос, только из распакованного архива (структура zip повторяет папку).
    public static class Migration
    {
        // Записи пользовательского списка без комментариев и заглушек
        public static int GetUserListCount(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                return File.ReadAllLines(path).Select(l => l.Trim())
                    .Count(l => l.Length > 0 && !l.StartsWith("#") && l != "domain.example.abc" && l != Ctx.IpPlaceholder);
            }
            catch { return 0; }
        }

        static bool SameHash(string a, string b)
        {
            try
            {
                using var sha = SHA256.Create();
                byte[] Hash(string p) { using var fs = File.OpenRead(p); return sha.ComputeHash(fs); }
                return StructuralComparisons.StructuralEqualityComparer.Equals(Hash(a), Hash(b));
            }
            catch { return false; }
        }

        // Что можно перенести из старой папки: Id, заголовок, подробность, включено ли по умолчанию
        public static List<MigrItem> GetMigrationItems(string old, bool fromArchive = false, string[] archiveItems = null)
        {
            bool withZapret = !fromArchive || (archiveItems ?? Array.Empty<string>()).Contains("zapret");
            var items = new List<MigrItem>();
            void Add(string id, string title, string detail, bool on) => items.Add(new MigrItem { Id = id, Title = title, Detail = detail, On = on });

            foreach (var l in new[]
            {
                (Id: "list-general-user.txt", Title: "Свои домены"),
                (Id: "list-exclude-user.txt", Title: "Свои исключения доменов"),
                (Id: "ipset-exclude-user.txt", Title: "Свои исключения IP"),
            })
            {
                string src = Path.Combine(old, "lists", l.Id);
                string dst = Path.Combine(Ctx.ListsDir, l.Id);
                int n = GetUserListCount(src);
                bool same = File.Exists(dst) && File.Exists(src) && SameHash(src, dst);
                if (n > 0 && !same) Add(l.Id, l.Title, $"lists\\{l.Id} — записей: {n} (здесь: {GetUserListCount(dst)})", true);
            }

            string stOld = Path.Combine(old, "utils", "ui-state.json");
            if (File.Exists(stOld))
            {
                string detail = "тема, акцент, вопросы перед запуском и тестом";
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(stOld));
                    var r = doc.RootElement;
                    int h = r.TryGetProperty("History", out var hh) && hh.ValueKind == JsonValueKind.Array ? hh.GetArrayLength() : 0;
                    string last = r.TryGetProperty("LastStrategy", out var ls) && ls.ValueKind == JsonValueKind.String ? ls.GetString() : null;
                    detail = $"история ({h} записей), последняя стратегия {last}, тема и вопросы";
                }
                catch { }
                // В новой папке уже есть своя история — не затираем её по умолчанию
                bool fresh = Ctx.UiState.History.Count <= 1;
                if (!fresh) detail += ". Заменит историю этой папки";
                Add("ui-state", "История и настройки окна", $"utils\\ui-state.json — {detail}", fresh);
            }

            string tOld = Path.Combine(old, "utils", "targets.txt");
            if (File.Exists(tOld))
            {
                string tDefOld = Path.Combine(old, "utils", "targets.default.txt");
                bool same = SameHash(tOld, Ctx.TargetsTxt) && File.Exists(Ctx.TargetsTxt);
                if (!same)
                {
                    bool edited = File.Exists(tDefOld) && !SameHash(tOld, tDefOld);
                    string groups = string.Join(", ", Targets.ReadTargetsModel(tOld).Groups.Select(g => g.Name));
                    if (edited) Add("targets", "Настройки теста", $"utils\\targets.txt — вы меняли список: {groups}", true);
                    else Add("targets", "Настройки теста", $"utils\\targets.txt — похоже, стандартный список старой версии: {groups}", false);
                }
            }

            string where = fromArchive ? "в архиве" : "в старой папке";
            if (withZapret)
            {
                var og = Zapret.ReadGameFilterFile(Path.Combine(old, "utils", "game_filter.enabled"));
                var cur = Zapret.GetServiceGameFilter();
                bool rangesDiffer = (og.Tcp != null && og.Tcp != cur.TcpRange) || (og.Udp != null && og.Udp != cur.UdpRange);
                if (og.Mode != cur.Mode || rangesDiffer)
                {
                    string ports = (og.Tcp != null || og.Udp != null) ? $" (порты TCP {og.Tcp}, UDP {og.Udp})" : "";
                    Add("game", "Игровой фильтр", $"{where}: {og.Mode}{ports}, здесь: {cur.Mode}", true);
                }

                bool oldUpd = File.Exists(Path.Combine(old, "utils", "check_updates.enabled"));
                if (oldUpd != File.Exists(Ctx.UpdFlag))
                    Add("updates", "Проверка обновлений", $"{where}: {(oldUpd ? "включена" : "выключена")}", true);
            }

            string oldIpFile = Path.Combine(old, "lists", "ipset-all.txt");
            if (File.Exists(oldIpFile))
            {
                string raw = File.ReadAllText(oldIpFile).Trim('\uFEFF', ' ', '\r', '\n', '\t');
                string oldIp = raw.Length == 0 ? "any" : raw.Contains(Ctx.IpPlaceholder) ? "none" : "loaded";
                if (oldIp != Zapret.GetIpsetStatus()) Add("ipset", "Режим IPset", $"{where}: {oldIp}, здесь: {Zapret.GetIpsetStatus()}", true);
            }

            string resOld = Path.Combine(old, "utils", "test results");
            if (Directory.Exists(resOld))
            {
                int n;
                try
                {
                    n = new DirectoryInfo(resOld).GetFiles("test_results_*.txt")
                        .Count(f => !File.Exists(Path.Combine(Ctx.ResultsDir, f.Name)));
                }
                catch { n = 0; }
                if (n > 0) Add("results", "Результаты тестов", $"utils\\test results — файлов: {n}", true);
            }
            return items;
        }

        // Копирует выбранное; заменяемые файлы этой папки сначала уходят в utils\migrate-backup-<время>
        public static MigrationResult InvokeMigration(string old, string[] ids)
        {
            string backup = Path.Combine(Ctx.UtilsDir, "migrate-backup-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            var result = new MigrationResult();
            void CopyIn(string rel)
            {
                string src = Path.Combine(old, rel);
                string dst = Path.Combine(Ctx.Root, rel);
                if (File.Exists(dst))
                {
                    string bk = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bk));
                    File.Copy(dst, bk, true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }

            foreach (var f in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
                if (ids.Contains(f)) { CopyIn(Path.Combine("lists", f)); result.Done.Add("lists\\" + f); }
            if (ids.Contains("ui-state"))
            {
                CopyIn(Path.Combine("utils", "ui-state.json"));
                // Служба из старой папки сюда не относится — не восстанавливаем её после теста по старому пути
                Ctx.UiState = UiState.Load();
                Ctx.UiState.PendingServiceRestore = null;
                Ctx.UiState.Save();
                result.Done.Add("история и настройки окна");
            }
            if (ids.Contains("targets"))
            {
                // Стандартный список новой версии остаётся для «Вернуть стандартные»
                if (File.Exists(Ctx.TargetsTxt))
                {
                    Directory.CreateDirectory(backup);
                    File.Copy(Ctx.TargetsTxt, Path.Combine(backup, "targets.txt"), true);
                    File.Copy(Ctx.TargetsTxt, Ctx.TargetsDefault, true);
                }
                File.Copy(Path.Combine(old, "utils", "targets.txt"), Ctx.TargetsTxt, true);
                result.Done.Add("настройки теста");
            }
            if (ids.Contains("game"))
            {
                // По смыслу, а не копией файла: формат файла у версий zapret разный
                if (File.Exists(Ctx.GameFlag))
                {
                    Directory.CreateDirectory(Path.Combine(backup, "utils"));
                    File.Copy(Ctx.GameFlag, Path.Combine(backup, "utils", "game_filter.enabled"), true);
                }
                var og = Zapret.ReadGameFilterFile(Path.Combine(old, "utils", "game_filter.enabled"));
                Zapret.SetGameFilter(og.Mode, og.Tcp, og.Udp);
                result.Done.Add($"игровой фильтр: {og.Mode}");
            }
            if (ids.Contains("updates"))
            {
                if (File.Exists(Path.Combine(old, "utils", "check_updates.enabled"))) Fs.WriteTextFile(Ctx.UpdFlag, new[] { "ENABLED" });
                else try { File.Delete(Ctx.UpdFlag); } catch { }
                result.Done.Add("проверка обновлений");
            }
            if (ids.Contains("ipset"))
            {
                string raw = File.ReadAllText(Path.Combine(old, "lists", "ipset-all.txt")).Trim('\uFEFF', ' ', '\r', '\n', '\t');
                string mode = raw.Length == 0 ? "any" : raw.Contains(Ctx.IpPlaceholder) ? "none" : "loaded";
                try { Zapret.SetIpsetStatus(mode); result.Done.Add($"режим IPset: {mode}"); }
                catch (Exception ex) { result.Done.Add($"режим IPset не перенесён: {ex.Message}"); }
            }
            if (ids.Contains("results"))
            {
                Directory.CreateDirectory(Ctx.ResultsDir);
                int n = 0;
                foreach (var f in new DirectoryInfo(Path.Combine(old, "utils", "test results")).GetFiles("test_results_*.txt"))
                {
                    string dst = Path.Combine(Ctx.ResultsDir, f.Name);
                    if (!File.Exists(dst)) { File.Copy(f.FullName, dst); n++; }
                }
                result.Done.Add($"результаты тестов: {n}");
            }
            if (Directory.Exists(backup)) result.Backup = backup;
            return result;
        }

        // ---------- export / import ----------

        public const string ExportManifest = "zapret-ui-export.json";

        public static List<MigrItem> GetExportItems()
        {
            var items = new List<MigrItem>();
            var lists = new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" }
                .Select(f => (f, n: GetUserListCount(Path.Combine(Ctx.ListsDir, f))))
                .Where(x => x.n > 0).Select(x => $"{x.f} ({x.n})").ToArray();
            if (lists.Length > 0) items.Add(new MigrItem { Id = "lists", Title = "Свои списки", Detail = "lists\\ — " + string.Join(", ", lists), On = true });
            items.Add(new MigrItem { Id = "ui-state", Title = "История и настройки окна", Detail = $"история ({Ctx.UiState.History.Count} записей), последняя стратегия, тема и вопросы", On = true });
            if (File.Exists(Ctx.TargetsTxt))
            {
                string groups = string.Join(", ", Targets.ReadTargetsModel(Ctx.TargetsTxt).Groups.Select(g => g.Name));
                items.Add(new MigrItem { Id = "targets", Title = "Настройки теста", Detail = $"utils\\targets.txt — {groups}", On = true });
            }
            items.Add(new MigrItem
            {
                Id = "zapret",
                Title = "Настройки zapret",
                Detail = $"игровой фильтр: {Zapret.GetServiceGameFilter().Mode}, IPset: {Zapret.GetIpsetStatus()}, проверка обновлений: {(File.Exists(Ctx.UpdFlag) ? "вкл" : "выкл")}",
                On = true,
            });
            var res = ResultsModel.GetResultFiles();
            if (res.Length > 0) items.Add(new MigrItem { Id = "results", Title = "Результаты тестов", Detail = $"utils\\test results — файлов: {res.Length}", On = false });
            items.Add(new MigrItem { Id = "ui", Title = "Само окно", Detail = "zapret-ui.exe — чтобы отправить другу вместе с настройками", On = false });
            return items;
        }

        // Собирает архив; ids — какие пункты из GetExportItems включить
        public static void NewSettingsArchive(string zipPath, string[] ids)
        {
            string stage = Path.Combine(Path.GetTempPath(), "zapret-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(stage, "lists"));
            Directory.CreateDirectory(Path.Combine(stage, "utils"));
            try
            {
                void Put(string from, string rel)
                {
                    if (!File.Exists(from)) return;
                    string dst = Path.Combine(stage, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(from, dst, true);
                }
                if (ids.Contains("lists"))
                    foreach (var f in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
                        Put(Path.Combine(Ctx.ListsDir, f), Path.Combine("lists", f));
                if (ids.Contains("ui-state"))
                {
                    // Без служебного пути к службе этого компьютера
                    var copy = UiState.Load();
                    copy.PendingServiceRestore = null;
                    string json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(Path.Combine(stage, "utils", "ui-state.json"), json);
                }
                if (ids.Contains("targets")) { Put(Ctx.TargetsTxt, Path.Combine("utils", "targets.txt")); Put(Ctx.TargetsDefault, Path.Combine("utils", "targets.default.txt")); }
                if (ids.Contains("zapret"))
                {
                    Put(Ctx.GameFlag, Path.Combine("utils", "game_filter.enabled"));
                    Put(Ctx.UpdFlag, Path.Combine("utils", "check_updates.enabled"));
                    // Режим IPset, а не сам список: none — заглушка, any — пусто, loaded — пометка
                    string mode = Zapret.GetIpsetStatus();
                    string marker = mode == "none" ? Ctx.IpPlaceholder : mode == "any" ? "" : "# loaded";
                    File.WriteAllText(Path.Combine(stage, "lists", "ipset-all.txt"), marker);
                }
                if (ids.Contains("results"))
                {
                    Directory.CreateDirectory(Path.Combine(stage, "utils", "test results"));
                    foreach (var f in ResultsModel.GetResultFiles()) File.Copy(f.FullName, Path.Combine(stage, "utils", "test results", f.Name));
                }
                if (ids.Contains("ui"))
                {
                    Put(Ctx.DistributeExe, "zapret-ui.exe");
                }
                var manifest = new Dictionary<string, object>
                {
                    ["Created"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["ZapretVersion"] = Zapret.GetLocalVersion(),
                    ["Items"] = ids,
                };
                File.WriteAllText(Path.Combine(stage, ExportManifest), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                // Вручную, а не CreateFromDirectory: пути внутри архива с «/»
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var f in new DirectoryInfo(stage).GetFiles("*", SearchOption.AllDirectories))
                    {
                        string rel = f.FullName.Substring(stage.Length + 1).Replace('\\', '/');
                        zip.CreateEntryFromFile(f.FullName, rel);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(stage, true); } catch { }
            }
        }
    }
}
