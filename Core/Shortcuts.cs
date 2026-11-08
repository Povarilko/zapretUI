using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ZapretUi
{
    // Ярлык «zapret» запускает окно напрямую. Иконка — файл utils\zapret-ui-<хеш>.ico:
    // Проводник кэширует иконки по пути, и новая картинка под старым именем показывалась бы старой
    public static class Shortcuts
    {
        public const string ShortcutName = "zapret.lnk";

        static byte[] IconBytes()
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("ZapretUi.app.ico");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public static string GetIconPath()
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(IconBytes());
            var sb = new StringBuilder();
            for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
            return Path.Combine(Ctx.UtilsDir, "zapret-ui-" + sb + ".ico");
        }

        static void EnsureIconFile()
        {
            string ico = GetIconPath();
            if (!File.Exists(ico))
            {
                Directory.CreateDirectory(Ctx.UtilsDir);
                File.WriteAllBytes(ico, IconBytes());
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        const uint STGM_READ = 0x0;

        static IShellLinkW CreateLink() => (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));

        public static string CreateShortcut(string folder)
        {
            EnsureIconFile();
            string ico = GetIconPath();
            string lnkPath = Path.Combine(folder, ShortcutName);
            var link = CreateLink();
            link.SetPath(Ctx.ExePath);
            link.SetArguments($"-ZapretRoot \"{Ctx.Root}\"");
            link.SetWorkingDirectory(Ctx.Root);
            link.SetIconLocation(ico, 0);
            link.SetDescription($"zapret {Zapret.GetLocalVersion()} — окно управления");
            ((IPersistFile)link).Save(lnkPath, true);
            return lnkPath;
        }

        public class ShortcutInfo
        {
            public string TargetPath;
            public string Arguments;
            public string IconLocation;
        }

        public static ShortcutInfo ReadShortcut(string path)
        {
            var link = CreateLink();
            ((IPersistFile)link).Load(path, STGM_READ);
            var sbTarget = new StringBuilder(260);
            link.GetPath(sbTarget, sbTarget.Capacity, IntPtr.Zero, 0);
            var sbArgs = new StringBuilder(1024);
            link.GetArguments(sbArgs, sbArgs.Capacity);
            var sbIcon = new StringBuilder(260);
            link.GetIconLocation(sbIcon, sbIcon.Capacity, out _);
            return new ShortcutInfo { TargetPath = sbTarget.ToString(), Arguments = sbArgs.ToString(), IconLocation = sbIcon.ToString() };
        }

        static readonly (string Name, Environment.SpecialFolder Folder)[] Places =
        {
            ("на рабочем столе", Environment.SpecialFolder.DesktopDirectory),
            ("в меню «Пуск»", Environment.SpecialFolder.Programs),
        };

        static bool PointsToRoot(ShortcutInfo info, string root)
        {
            string prefix = root.TrimEnd('\\') + "\\";
            bool target = string.Equals(info.TargetPath, Ctx.ExePath, StringComparison.OrdinalIgnoreCase);
            bool oldArgs = info.Arguments.IndexOf(prefix + "zapret-ui.ps1", StringComparison.OrdinalIgnoreCase) >= 0;
            bool oldTarget = string.Equals(info.TargetPath, Path.Combine(root, "zapret-ui.exe"), StringComparison.OrdinalIgnoreCase);
            return target || oldArgs || oldTarget;
        }

        // Первый запуск: ярлык в меню «Пуск», чтобы окно находилось поиском. Только один раз
        public static void InitializeStartShortcut()
        {
            if (Ctx.UiState.StartShortcutDone) return;
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!File.Exists(Path.Combine(folder, ShortcutName))) CreateShortcut(folder);
            Ctx.UiState.StartShortcutDone = true;
            Ctx.UiState.Save();
        }

        // Иконка сменилась — ярлыки этой папки переводим на файл с новым именем, старые файлы иконок удаляем
        public static void UpdateShortcutIcons()
        {
            string ico = GetIconPath();
            foreach (var place in Places)
            {
                string lnkPath = Path.Combine(Environment.GetFolderPath(place.Folder), ShortcutName);
                if (!File.Exists(lnkPath)) continue;
                ShortcutInfo info;
                try { info = ReadShortcut(lnkPath); } catch { continue; }
                if (!PointsToRoot(info, Ctx.Root)) continue;
                if (!string.Equals(info.IconLocation, ico + ",0", StringComparison.OrdinalIgnoreCase) || !File.Exists(ico))
                    CreateShortcut(Environment.GetFolderPath(place.Folder));
            }
            if (!Directory.Exists(Ctx.UtilsDir)) return;
            foreach (var f in Directory.GetFiles(Ctx.UtilsDir, "zapret-ui*.ico"))
                if (!string.Equals(f, ico, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }

        // После автообновления: ярлыки, ведущие в старую папку, переводим на эту. Возвращает, где переведены
        public static List<string> UpdateShortcutAfterUpdate(string old)
        {
            var moved = new List<string>();
            foreach (var place in Places)
            {
                string lnkPath = Path.Combine(Environment.GetFolderPath(place.Folder), ShortcutName);
                if (!File.Exists(lnkPath)) continue;
                ShortcutInfo info;
                try { info = ReadShortcut(lnkPath); } catch { continue; }
                string prefix = old.TrimEnd('\\') + "\\";
                bool oldPs = info.Arguments.IndexOf(prefix + "zapret-ui.ps1", StringComparison.OrdinalIgnoreCase) >= 0;
                bool oldExe = string.Equals(info.TargetPath, Path.Combine(old, "zapret-ui.exe"), StringComparison.OrdinalIgnoreCase);
                if (!oldPs && !oldExe) continue;
                CreateShortcut(Environment.GetFolderPath(place.Folder));
                moved.Add(place.Name);
            }
            return moved;
        }
    }
}
