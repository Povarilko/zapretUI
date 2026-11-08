using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZapretUi
{
    public class HistoryEntry
    {
        public string Time { get; set; }
        public string Event { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
    }

    public class PendingRestore
    {
        public string ImagePath { get; set; }
        public string Strategy { get; set; }
        public string Description { get; set; }
    }

    // utils\ui-state.json — тот же формат, что пишет PowerShell-версия (PascalCase, без отступов не важно)
    public class UiState
    {
        public string LastStrategy { get; set; }
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        public bool AutoRemoveServiceForTest { get; set; }
        public PendingRestore PendingServiceRestore { get; set; }
        public string ThemeMode { get; set; } = "system";
        public bool SystemAccent { get; set; } = true;
        public string ServiceStartAction { get; set; } = "ask";
        public string SkippedVersion { get; set; }
        public bool DetailOpen { get; set; }
        public bool StartShortcutDone { get; set; }
        public bool TestBaseline { get; set; } = true;
        public Dictionary<string, double> TestPace { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public const int HistoryLimit = 300;

        static string GetStr(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool GetBool(JsonElement e, string name, bool def = false)
            => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;

        public static UiState Load()
        {
            var s = new UiState();
            if (!File.Exists(Ctx.StateFile)) return s;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Ctx.StateFile));
                var r = doc.RootElement;
                s.LastStrategy = GetStr(r, "LastStrategy");
                if (r.TryGetProperty("History", out var h) && h.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in h.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        s.History.Add(new HistoryEntry
                        {
                            Time = GetStr(e, "Time"),
                            Event = GetStr(e, "Event"),
                            Name = GetStr(e, "Name"),
                            Source = GetStr(e, "Source"),
                        });
                    }
                }
                s.AutoRemoveServiceForTest = GetBool(r, "AutoRemoveServiceForTest");
                if (r.TryGetProperty("PendingServiceRestore", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ImagePath", out _))
                {
                    s.PendingServiceRestore = new PendingRestore
                    {
                        ImagePath = GetStr(p, "ImagePath"),
                        Strategy = GetStr(p, "Strategy"),
                        Description = GetStr(p, "Description"),
                    };
                }
                var tm = GetStr(r, "ThemeMode");
                if (tm == "system" || tm == "light" || tm == "dark") s.ThemeMode = tm;
                s.SystemAccent = GetBool(r, "SystemAccent", true);
                s.ServiceStartAction = GetStr(r, "ServiceStartAction") == "auto" ? "auto" : "ask";
                s.SkippedVersion = GetStr(r, "SkippedVersion");
                s.DetailOpen = GetBool(r, "DetailOpen");
                s.StartShortcutDone = GetBool(r, "StartShortcutDone");
                s.TestBaseline = GetBool(r, "TestBaseline", true);
                if (r.TryGetProperty("TestPace", out var tp) && tp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pp in tp.EnumerateObject())
                        if (pp.Value.ValueKind == JsonValueKind.Number && pp.Value.TryGetDouble(out double d) && d > 0)
                            s.TestPace[pp.Name] = d;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Ctx.UtilsDir);
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("LastStrategy", LastStrategy);
                    w.WriteBoolean("AutoRemoveServiceForTest", AutoRemoveServiceForTest);
                    w.WriteString("ThemeMode", ThemeMode);
                    w.WriteBoolean("SystemAccent", SystemAccent);
                    w.WriteString("ServiceStartAction", ServiceStartAction);
                    w.WriteString("SkippedVersion", SkippedVersion);
                    w.WriteBoolean("DetailOpen", DetailOpen);
                    w.WriteBoolean("StartShortcutDone", StartShortcutDone);
                    w.WriteBoolean("TestBaseline", TestBaseline);
                    w.WritePropertyName("TestPace");
                    w.WriteStartObject();
                    foreach (var kv in TestPace) w.WriteNumber(kv.Key, kv.Value);
                    w.WriteEndObject();
                    w.WritePropertyName("PendingServiceRestore");
                    if (PendingServiceRestore != null)
                    {
                        w.WriteStartObject();
                        w.WriteString("ImagePath", PendingServiceRestore.ImagePath);
                        w.WriteString("Strategy", PendingServiceRestore.Strategy);
                        w.WriteString("Description", PendingServiceRestore.Description);
                        w.WriteEndObject();
                    }
                    else w.WriteNullValue();
                    w.WritePropertyName("History");
                    w.WriteStartArray();
                    foreach (var e in History)
                    {
                        w.WriteStartObject();
                        w.WriteString("Time", e.Time);
                        w.WriteString("Event", e.Event);
                        w.WriteString("Name", e.Name);
                        w.WriteString("Source", e.Source);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                File.WriteAllBytes(Ctx.StateFile, ms.ToArray());
            }
            catch { }
        }

        public void SetLastStrategy(string name)
        {
            if (string.IsNullOrEmpty(name) || LastStrategy == name) return;
            LastStrategy = name;
            Save();
        }

        // Два старта подряд одной стратегии и стоп без старта — не события
        public bool AddHistoryEvent(string kind, string name, string source)
        {
            var last = History.Count > 0 ? History[History.Count - 1] : null;
            if (kind == "stop" && (last == null || last.Event == "stop")) return false;
            if (last != null && last.Event == "start" && kind == "start" && last.Name == name) return false;
            History.Add(new HistoryEntry { Time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), Event = kind, Name = name, Source = source });
            if (History.Count > HistoryLimit) History.RemoveRange(0, History.Count - HistoryLimit);
            Save();
            return true;
        }
    }
}
