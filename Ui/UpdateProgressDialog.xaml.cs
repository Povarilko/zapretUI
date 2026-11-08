using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace ZapretUi
{
    public partial class UpdateProgressDialog : Window
    {
        string _stage = "asset";
        string _version;
        readonly string _into;
        ReleaseAsset _asset;
        WebClient _wc;
        System.Threading.Tasks.Task _task;
        string _zip;
        string _stageDir;
        string _target;
        public string ResultPath;
        bool _failed;

        UpdateProgressDialog(string version, string into)
        {
            InitializeComponent();
            DialogBase.Init(this);
            _version = version;
            _into = into;
            bool installing = into != null;
            Title = installing ? "Установка zapret" : "Обновление zapret";
            UpTitle.Text = installing ? "Установка zapret" : $"Обновление до {version}";
            // При установке главного окна ещё нет
            if (Ctx.Main != null) Owner = Ctx.Main;
            else { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) => Tick(timer);
            UpCancel.Click += (s, e) =>
            {
                if (_wc != null && _stage == "download") _wc.CancelAsync();
                timer.Stop();
                if (!_failed) Fail(installing ? "Установка отменена." : "Обновление отменено.");
                DialogResult = false;
            };
            Loaded += (s, e) => timer.Start();
            Closed += (s, e) => timer.Stop();
        }

        void Cleanup()
        {
            try { if (_stageDir != null && Directory.Exists(_stageDir)) Directory.Delete(_stageDir, true); } catch { }
            try { if (_zip != null && File.Exists(_zip)) File.Delete(_zip); } catch { }
        }

        void Fail(string msg)
        {
            _failed = true;
            UpErr.Text = msg;
            UpErr.Visibility = Visibility.Visible;
            UpStep.Text = _into != null ? "Установка остановлена." : "Обновление остановлено. Эта папка и служба не изменились.";
            UpCancel.Content = "Закрыть";
            Cleanup();
        }

        void Tick(DispatcherTimer timer)
        {
            if (_failed) { timer.Stop(); return; }
            try
            {
                switch (_stage)
                {
                    case "asset":
                        UpStep.Text = "Ищу архив релиза на GitHub…";
                        UpBar.IsIndeterminate = true;
                        // Для установки версия заранее неизвестна — берём её там же, где проверка обновлений
                        if (string.IsNullOrEmpty(_version))
                        {
                            try { _version = new WebClient().DownloadString(Updater.VersionUrl).Trim(); } catch { }
                        }
                        _asset = Updater.GetReleaseAsset(_version);
                        _zip = Path.Combine(Path.GetTempPath(), _asset.Name);
                        try { if (File.Exists(_zip)) File.Delete(_zip); } catch { }
                        _wc = new WebClient();
                        _wc.Headers["User-Agent"] = "zapret-ui";
                        _task = _wc.DownloadFileTaskAsync(_asset.Url, _zip);
                        _stage = "download";
                        break;
                    case "download":
                        long got = File.Exists(_zip) ? new FileInfo(_zip).Length : 0;
                        if (_asset.Size > 0)
                        {
                            UpBar.IsIndeterminate = false;
                            UpBar.Value = Math.Min(100, 100.0 * got / _asset.Size);
                        }
                        UpStep.Text = "Скачиваю " + _asset.Name + " — " + Math.Round(got / 1048576.0, 1) + " МБ";
                        if (!_task.IsCompleted) return;
                        if (_task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                        {
                            string err = _task.Exception?.InnerException?.Message ?? "скачивание отменено";
                            Fail("Не удалось скачать архив: " + err);
                            return;
                        }
                        _stage = "extract";
                        break;
                    case "extract":
                        UpBar.IsIndeterminate = true;
                        UpStep.Text = "Распаковываю…";
                        // Обновление: распаковка рядом с целевой папкой (тот же диск), потом переименование.
                        // Установка: во временную папку Windows — у неё короткий путь (лимит пути 260 символов)
                        string stageBase;
                        if (_into != null)
                        {
                            Directory.CreateDirectory(_into);
                            stageBase = Path.GetTempPath();
                        }
                        else stageBase = Updater.GetUpdateBaseFolder();
                        _stageDir = Path.Combine(stageBase, ".zu-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                        ZipFile.ExtractToDirectory(_zip, _stageDir);
                        // Папка с service.bat: сама stageDir или её первая подпапка
                        string newRoot = new[] { _stageDir }.Concat(Directory.GetDirectories(_stageDir))
                            .FirstOrDefault(d => File.Exists(Path.Combine(d, "service.bat")));
                        if (newRoot == null)
                        {
                            Fail("В архиве не найден service.bat — это не похоже на релиз zapret-discord-youtube.");
                            return;
                        }
                        // Антивирусы любят удалять winws.exe и драйвер WinDivert сразу после распаковки
                        foreach (var f in new[] { @"bin\winws.exe", @"bin\WinDivert64.sys" })
                        {
                            if (!File.Exists(Path.Combine(newRoot, f)))
                            {
                                Fail($"После распаковки нет файла {f} — скорее всего, его удалил антивирус. Добавьте папку {stageBase} в исключения антивируса и попробуйте снова.");
                                return;
                            }
                        }
                        string target;
                        if (_into != null)
                        {
                            // В папке уже лежит окно — дописываем файлы релиза к ним; само окно не заменяем
                            target = _into;
                            try { File.Delete(Path.Combine(newRoot, "zapret-ui.exe")); } catch { }
                            try { File.Delete(Path.Combine(newRoot, "zapret-ui.ps1")); } catch { }
                            try { File.Delete(Path.Combine(newRoot, "service-ui.bat")); } catch { }
                            Zapret.CopyTree(newRoot, target);
                        }
                        else
                        {
                            string baseFolder = Updater.GetUpdateBaseFolder();
                            target = Path.Combine(baseFolder, "zapret-discord-youtube-" + _asset.Version);
                            int n = 2;
                            while (Directory.Exists(target)) { target = Path.Combine(baseFolder, $"zapret-discord-youtube-{_asset.Version}-{n}"); n++; }
                            Directory.Move(newRoot, target);
                        }
                        Cleanup();
                        if (!Zapret.TestSameFolder(target, Ctx.Root))
                        {
                            File.Copy(Ctx.DistributeExe, Path.Combine(target, "zapret-ui.exe"), true);
                        }
                        _target = target;
                        ResultPath = target;
                        timer.Stop();
                        UpBar.IsIndeterminate = false;
                        UpBar.Value = 100;
                        DialogResult = true;
                        break;
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // Возвращает путь новой папки или null (отмена/ошибка). Into — установка в эту папку вместо новой рядом
        public static string Show(string version, string into = null)
        {
            var d = new UpdateProgressDialog(version, into);
            d.ShowDialog();
            return d.ResultPath;
        }
    }
}
