using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ZapretUi
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Загрузчик запускает приложение из своей папки, передавая папку zapret параметром
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i].Equals("-ZapretRoot", StringComparison.OrdinalIgnoreCase)) Ctx.Root = e.Args[i + 1].TrimEnd('\\');
                if (e.Args[i].Equals("-Launcher", StringComparison.OrdinalIgnoreCase)) Ctx.LauncherPath = e.Args[i + 1];
            }
            // exe, запущенный через dotnet.exe: ProcessPath указывает на dotnet — берем путь сборки
            if (Ctx.ExePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(loc)) Ctx.ExePath = loc;
            }

            // Права администратора нужны для запуска winws.exe и чтения его командной строки
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                string args = string.Join(" ", e.Args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                try
                {
                    Process.Start(new ProcessStartInfo(Ctx.ExePath, args)
                    {
                        Verb = "runas",
                        UseShellExecute = true,
                        WorkingDirectory = Ctx.Root,
                    });
                }
                catch { }
                Shutdown();
                return;
            }

            // Своё имя приложения для панели задач: иначе окно группируется с консолью
            try { Native.SetCurrentProcessExplicitAppUserModelID("Flowseal.zapret.ui"); } catch { }

            // Иконка не зависит от темы — загружаем один раз
            try
            {
                var asm = typeof(App).Assembly;
                using var stream = asm.GetManifestResourceStream("ZapretUi.app.ico");
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Ctx.AppIcon = decoder.Frames[0];
            }
            catch { }

            Theme.Init();
            Theme.AddResources(Resources);
            Ctx.UiState = UiState.Load();
            Theme.Sync();

            // Окно лежит без zapret — предлагаем скачать его (или открыть уже установленный)
            if (!Zapret.TestZapretFolder(Ctx.Root))
            {
                if (!InstallDialog.ShowInstallFlow()) { Shutdown(); return; }
            }

            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i].Equals("-MigrateFrom", StringComparison.OrdinalIgnoreCase))
                    Ctx.MigrateFrom = e.Args[i + 1];

            var main = new MainWindow();
            Ctx.Main = main;
            MainWindow = main;
            main.Show();
        }
    }
}
