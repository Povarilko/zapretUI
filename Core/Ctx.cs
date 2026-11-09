using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace ZapretUi
{
    // Общее состояние окна — то, что в PowerShell-версии жило в $script:
    public static class Ctx
    {
        public static string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string BinDir => Path.Combine(Root, "bin");
        public static string ListsDir => Path.Combine(Root, "lists");
        public static string UtilsDir => Path.Combine(Root, "utils");
        public static string ResultsDir => Path.Combine(UtilsDir, "test results");
        public static string ServiceBat => Path.Combine(Root, "service.bat");
        public static string TestPs => Path.Combine(UtilsDir, "test zapret.ps1");
        public static string TargetsTxt => Path.Combine(UtilsDir, "targets.txt");
        public static string GameFlag => Path.Combine(UtilsDir, "game_filter.enabled");
        public static string UpdFlag => Path.Combine(UtilsDir, "check_updates.enabled");
        public static string IpsetFile => Path.Combine(ListsDir, "ipset-all.txt");
        public static string IpsetBak => Path.Combine(ListsDir, "ipset-all.txt.backup");
        public const string IpsetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        public const string IpPlaceholder = "203.0.113.113/32";
        public const string BaselineName = "0-no-bypass";
        public static string BaselineBat => Path.Combine(Root, BaselineName + ".bat");
        public static string SvcKey => @"SYSTEM\CurrentControlSet\Services\zapret";
        public static string StateFile => Path.Combine(UtilsDir, "ui-state.json");
        public static string TestLog => Path.Combine(UtilsDir, "test-ui.log");
        public static string TestInput => Path.Combine(UtilsDir, "test-ui.input");
        public static string TargetsDefault => Path.Combine(UtilsDir, "targets.default.txt");

        public static string ExePath = Environment.ProcessPath;
        // Загрузчик (bootstrapper), которым распространяется приложение; его и копируем при переездах/обновлении
        public static string LauncherPath = null;
        public static string DistributeExe => LauncherPath ?? ExePath;
        public static string MigrateFrom = null;
        public static ImageSource AppIcon = null;

        public static UiState UiState = new UiState();
        public static MainWindow Main = null;

        public static Strategy[] Strategies = Array.Empty<Strategy>();
        public static ZapretState State = null;
        public static TestRun Run = null;
        public static ResultSet Results = null;
        public static ResultSet LatestResults = null;
        public static FileInfo[] ResultFiles = Array.Empty<FileInfo>();
        public static string SelectedResult = null;
        public static bool DetailOpen = false;
        public static bool DetailDpiOpen = false;
        public static bool Loading = false;
        public static bool StartIsStop = false;
        public static string StartTip = null;
        public static string PrevLastName = null;
        public static string PendingSource = null;
        public static DateTime GraceUntil = DateTime.MinValue;
        public static string LastPids = "";
        public static string LastDetected = null;
        public static DateTime TestStartedAt = DateTime.MinValue;
        public static string NewVersion = null;
        public static readonly Dictionary<string, string> ResultKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static List<string> DiagConflicts = new List<string>();
        public static string PrevUiStateJson = null;
        public static bool AutoTuneActive = false;
    }
}
