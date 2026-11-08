using System;
using System.Windows;
using System.Windows.Controls;

namespace ZapretUi
{
    // ComboBox с FitContent=true подстраивает ширину поля под самый длинный пункт:
    // раскрытый список равен полю по ширине (шаблон темы), текст не обрезается
    public static class ComboFitWidth
    {
        public static bool GetFitContent(DependencyObject obj) => (bool)obj.GetValue(FitContentProperty);

        public static void SetFitContent(DependencyObject obj, bool value) => obj.SetValue(FitContentProperty, value);

        public static readonly DependencyProperty FitContentProperty = DependencyProperty.RegisterAttached(
            "FitContent", typeof(bool), typeof(ComboFitWidth),
            new FrameworkPropertyMetadata(false, OnFitContentChanged));

        static void OnFitContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ComboBox cb || !(bool)e.NewValue) return;
            double floor = cb.Width; // проектная ширина — минимум
            cb.DropDownOpened += (s, _) => Update(cb, floor);
            cb.Loaded += (s, _) => Update(cb, floor);
        }

        static void Update(ComboBox cb, double floor)
        {
            double text = 0;
            foreach (var item in cb.Items)
            {
                string s = item switch
                {
                    string str => str,
                    ComboBoxItem cbi => cbi.Content as string ?? cbi.Content?.ToString(),
                    _ => item?.ToString(),
                };
                if (string.IsNullOrEmpty(s)) continue;
                var tb = new TextBlock
                {
                    Text = s,
                    FontFamily = cb.FontFamily,
                    FontSize = cb.FontSize,
                    FontWeight = cb.FontWeight,
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                text = Math.Max(text, tb.DesiredSize.Width);
            }
            // Поле текста в шаблоне: Margin 11,0,32,0 у ContentPresenter + рамка
            double needed = text > 0 ? text + 46 : floor;
            double width = Math.Max(floor, needed);
            if (Math.Abs(cb.Width - width) > 1) cb.Width = width;
        }
    }
}
