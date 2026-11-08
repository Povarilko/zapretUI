using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ZapretUi
{
    // Палитры двух тем. Кисти Pal привязаны к кистям-источникам PalSrc: при смене темы меняется
    // только цвет у источников, и всё перекрашивается само (DynamicResource в стилях).
    public static class Theme
    {
        public static readonly Dictionary<string, SolidColorBrush> Pal = new Dictionary<string, SolidColorBrush>();
        static readonly Dictionary<string, SolidColorBrush> PalSrc = new Dictionary<string, SolidColorBrush>();

        static Dictionary<string, string> T(string bg, string surface, string surfaceAlt, string input, string inputHover,
            string border, string line, string text, string muted, string warn, string accent, string accentHover,
            string hover, string pressed, string sel, string selText, string track, string thumb, string thumbHover,
            string goodBg, string goodFg, string good, string midBg, string midFg, string mid,
            string badBg, string badFg, string bad, string naBg, string naFg)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bg"] = bg, ["Surface"] = surface, ["White"] = surface, ["SurfaceAlt"] = surfaceAlt,
                ["Input"] = input, ["InputHover"] = inputHover,
                ["Border"] = border, ["Line"] = line, ["Text"] = text, ["Muted"] = muted, ["Warn"] = warn,
                ["Accent"] = accent, ["AccentHover"] = accentHover, ["OnAccent"] = "#FFFFFF",
                ["Hover"] = hover, ["Pressed"] = pressed, ["Sel"] = sel, ["SelText"] = selText,
                ["Track"] = track, ["Thumb"] = thumb, ["ThumbHover"] = thumbHover,
                ["GoodBg"] = goodBg, ["GoodFg"] = goodFg, ["Good"] = good,
                ["MidBg"] = midBg, ["MidFg"] = midFg, ["Mid"] = mid,
                ["BadBg"] = badBg, ["BadFg"] = badFg, ["Bad"] = bad,
                ["NaBg"] = naBg, ["NaFg"] = naFg,
            };
        }

        static readonly Dictionary<string, Dictionary<string, string>> Themes =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["light"] = T("#F3F3F3", "#FFFFFF", "#F9F9F9", "#FFFFFF", "#F9F9F9",
                    "#E5E5E5", "#EDEDED", "#1A1A1A", "#616161", "#8A5300", "#0067C0", "#1975C5",
                    "#0F000000", "#07000000", "#E8F1FB", "#0C447C", "#E6E6E6", "#9A9A9A", "#6A6A6A",
                    "#E1F5EE", "#085041", "#1D9E75", "#FAEEDA", "#633806", "#EF9F27",
                    "#FCEBEB", "#791F1F", "#E24B4A", "#F1EFE8", "#5F5E5A"),
                ["dark"] = T("#202020", "#2B2B2B", "#252525", "#2F2F2F", "#353535",
                    "#3A3A3A", "#353535", "#F3F3F3", "#ABABAB", "#FAC775", "#0078D4", "#1A86D9",
                    "#14FFFFFF", "#0AFFFFFF", "#1F3A52", "#B5D4F4", "#3A3A3A", "#7A7A7A", "#A8A8A8",
                    "#123D30", "#9FE1CB", "#2BB487", "#43310F", "#FAC775", "#EF9F27",
                    "#4A1F1F", "#F7C1C1", "#E86160", "#363636", "#B4B2A9"),
            };

        public static string Name = "light";
        static string _themeKey = "";

        public static SolidColorBrush GetBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        public static void Init()
        {
            foreach (var kv in Themes["light"])
            {
                var color = (Color)ColorConverter.ConvertFromString(kv.Value);
                var src = new SolidColorBrush(color);
                var brush = new SolidColorBrush(color);
                var bind = new Binding("Color") { Source = src };
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, bind);
                PalSrc[kv.Key] = src;
                Pal[kv.Key] = brush;
            }
        }

        public static void AddResources(ResourceDictionary res)
        {
            foreach (var kv in Pal) res[kv.Key] = kv.Value;
        }

        public static string SystemTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var v = key?.GetValue("AppsUseLightTheme");
                if (v is int i && i == 0) return "dark";
            }
            catch { }
            return "light";
        }

        // Палитра акцента Windows: 7 оттенков от светлого к тёмному, [3] — сам цвет из «Персонализации»
        public static string[] GetAccentPalette()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                var b = key?.GetValue("AccentPalette") as byte[];
                if (b == null || b.Length < 28) return null;
                var list = new string[7];
                for (int i = 0; i < 7; i++)
                    list[i] = string.Format("#{0:X2}{1:X2}{2:X2}", b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                return list;
            }
            catch { return null; }
        }

        static string TextOn(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            return lum > 0.75 ? "#000000" : "#FFFFFF";
        }

        public static void Apply(string name, string[] accent)
        {
            Name = name;
            var colors = new Dictionary<string, string>(Themes[name], StringComparer.OrdinalIgnoreCase);
            if (accent != null)
            {
                // В светлой теме заливка тёмным оттенком, в тёмной — основным цветом,
                // чтобы на кнопках в обеих темах был белый текст
                string fill, hover, selText;
                if (name == "light") { fill = accent[4]; hover = accent[3]; selText = accent[5]; }
                else { fill = accent[3]; hover = accent[2]; selText = accent[0]; }
                colors["Accent"] = fill;
                colors["AccentHover"] = hover;
                colors["OnAccent"] = TextOn(fill);
                colors["Sel"] = "#" + (name == "light" ? "24" : "40") + accent[3].Substring(1);
                colors["SelText"] = selText;
            }
            foreach (var kv in colors) PalSrc[kv.Key].Color = (Color)ColorConverter.ConvertFromString(kv.Value);
        }

        // ThemeMode: system | light | dark. В режиме system тема сверяется с Windows на каждом опросе статуса.
        public static void Sync()
        {
            var mode = Ctx.UiState?.ThemeMode ?? "system";
            string name = mode == "system" ? SystemTheme() : mode;
            var accent = (Ctx.UiState != null && Ctx.UiState.SystemAccent) ? GetAccentPalette() : null;
            var key = name + "|" + (accent != null ? string.Join(",", accent) : "");
            if (key == _themeKey) return;
            _themeKey = key;
            Apply(name, accent);
            if (Ctx.Main != null)
            {
                SetTitleBar(Ctx.Main);
                foreach (Window w in Ctx.Main.OwnedWindows) SetTitleBar(w);
            }
        }

        // Тёмный заголовок окна (Windows 10 2004+ / 11)
        public static void SetTitleBar(Window w)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = Name == "dark" ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
            }
            catch { }
        }
    }
}
