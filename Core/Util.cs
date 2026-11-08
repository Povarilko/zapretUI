using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZapretUi
{
    public static class Fs
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteTextFile(string path, string[] lines)
        {
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Utf8NoBom);
        }

        public static void WriteTextFile(string path, string text) => File.WriteAllText(path, text, Utf8NoBom);
    }

    public static class Str
    {
        // Сортировка «по-человечески»: числа в имени сравниваются как числа
        public static string NaturalKey(string name) => Regex.Replace(name, "\\d+", m => m.Value.PadLeft(8, '0'));

        public static int NaturalCompare(string a, string b) => string.CompareOrdinal(NaturalKey(a), NaturalKey(b));
    }

    public static class Ui
    {
        public static TextBlock Text(string text, double size = 13, string weight = "Normal", Brush fg = null)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = (FontWeight)new FontWeightConverter().ConvertFromString(weight),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = fg ?? Theme.Pal["Text"],
            };
            return t;
        }

        public static Border Badge(string text, Brush bg, Brush fg)
        {
            var b = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(text, 11, "Normal", fg),
            };
            return b;
        }

        public static (Brush bg, Brush fg, Brush solid) RatioColors(int ok, int total)
        {
            if (total <= 0) return (Theme.Pal["NaBg"], Theme.Pal["NaFg"], Theme.Pal["NaFg"]);
            double r = (double)ok / total;
            if (r >= 1) return (Theme.Pal["GoodBg"], Theme.Pal["GoodFg"], Theme.Pal["Good"]);
            if (r >= 0.5) return (Theme.Pal["MidBg"], Theme.Pal["MidFg"], Theme.Pal["Mid"]);
            return (Theme.Pal["BadBg"], Theme.Pal["BadFg"], Theme.Pal["Bad"]);
        }

        public static void Pill(Border border, TextBlock textBlock, string text, string kind)
        {
            textBlock.Text = text;
            switch (kind)
            {
                case "good": border.Background = Theme.Pal["GoodBg"]; textBlock.Foreground = Theme.Pal["GoodFg"]; break;
                case "mid": border.Background = Theme.Pal["MidBg"]; textBlock.Foreground = Theme.Pal["MidFg"]; break;
                case "bad": border.Background = Theme.Pal["BadBg"]; textBlock.Foreground = Theme.Pal["BadFg"]; break;
                default: border.Background = Theme.Pal["NaBg"]; textBlock.Foreground = Theme.Pal["NaFg"]; break;
            }
        }
    }

    public static class Safe
    {
        // Invoke-Safe: показать ошибку в стиле окна вместо падения обработчика
        public static void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { MessageDialog.Show(ex.Message, MessageKind.Warning); }
        }
    }
}
