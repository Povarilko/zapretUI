using System;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace ZapretUi
{
    public partial class InstallDialog : Window
    {
        string _target;
        string _result; // 'here' — zapret установлен в папку окна
        System.Threading.Tasks.Task<string> _versionTask;

        InstallDialog()
        {
            InitializeComponent();
            DialogBase.Init(this);
            _target = Updater.GetDefaultInstallFolder();

            // Номер последней версии — асинхронно, чтобы без интернета окно не подвисало
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                _versionTask = new WebClient().DownloadStringTaskAsync(Updater.VersionUrl);
            }
            catch { }
            var verTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            verTimer.Tick += (s, e) =>
            {
                var t = _versionTask;
                if (t != null && !t.IsCompleted) return;
                verTimer.Stop();
                InsVersion.Text = t != null && t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result.Trim().Length > 0
                    ? "Последняя версия: " + t.Result.Trim()
                    : "Не удалось узнать последнюю версию — проверьте подключение к интернету.";
            };
            Loaded += (s, e) => verTimer.Start();
            Closed += (s, e) => verTimer.Stop();

            InsBrowse.Click += (s, e) => Safe.Run(() =>
            {
                var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                string picked = FolderPicker.Show(owner, "Папка, куда установить zapret", _target);
                if (picked == null) return;
                _target = picked;
                ShowTarget();
            });

            InsExisting.Click += (s, e) => Safe.Run(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Выберите service.bat в папке zapret",
                    Filter = "service.bat|service.bat",
                };
                if (dlg.ShowDialog(this) != true) return;
                string dir = Path.GetDirectoryName(dlg.FileName);
                if (!Zapret.TestZapretFolder(dir))
                    throw new Exception($"В папке {dir} нет bin\\winws.exe — это не похоже на zapret-discord-youtube.");
                Updater.OpenZapretFolder(dir);
                Close();
            });

            InsInstall.Click += (s, e) => Safe.Run(() =>
            {
                string target = _target;
                if (!Zapret.TestZapretFolder(target))
                {
                    // Окно хода установки открывается поверх этого; при ошибке или отмене остаёмся здесь
                    if (UpdateProgressDialog.Show("", target) == null) return;
                }
                if (Zapret.TestSameFolder(target, Ctx.Root)) _result = "here";
                else Updater.OpenZapretFolder(target);
                Close();
            });

            ShowTarget();
        }

        void ShowTarget()
        {
            InsPath.Text = _target;
            InsPath.ToolTip = _target;
            if (Zapret.TestZapretFolder(_target))
            {
                // В выбранной папке zapret уже есть — просто открываем
                InsInstall.Content = "Открыть";
                InsNote.Text = "В этой папке zapret уже есть. Окно переедет туда и откроется оттуда.";
            }
            else
            {
                InsInstall.Content = "Скачать и установить";
                InsNote.Text = Zapret.TestSameFolder(_target, Ctx.Root)
                    ? "zapret распакуется сюда, рядом с этим окном."
                    : "zapret распакуется в эту папку, окно переедет туда и откроется уже оттуда.";
            }
        }

        // true — zapret установлен в папку окна, можно открывать главное окно
        public static bool ShowInstallFlow()
        {
            var d = new InstallDialog();
            d.ShowDialog();
            return d._result == "here";
        }
    }
}
