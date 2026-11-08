using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ZapretUi
{
    public class PickerGroup
    {
        public string Name;
        public string[] Values;
    }

    public partial class TargetsPickerDialog : Window
    {
        class Row
        {
            public string Name;
            public CheckBox GroupBox;
            public TextBlock Label;
            public List<CheckBox> Boxes;
            public StackPanel Kids;
            public TextBlock Glyph;
        }

        readonly List<Row> _rows = new List<Row>();

        TargetsPickerDialog(string title, string subtitle, List<PickerGroup> groups, string okText)
        {
            InitializeComponent();
            DialogBase.Init(this);
            Title = title;
            PkTitle.Text = title;
            PkSub.Text = subtitle;
            PkOk.Content = okText;
            var font = (System.Windows.Media.FontFamily)FindResource("IconFont");

            void UpdateCount()
            {
                int nAddr = 0, nGroup = 0;
                foreach (var r in _rows)
                {
                    int on = r.Boxes.Count(b => b.IsChecked == true);
                    r.GroupBox.IsChecked = on == r.Boxes.Count ? true : on > 0 ? (bool?)null : false;
                    r.Label.Text = $"{r.Name}   ·   {on} из {r.Boxes.Count}";
                    nAddr += on;
                    if (on > 0) nGroup++;
                }
                PkCount.Text = $"Выбрано адресов: {nAddr}, групп: {nGroup}";
                PkOk.IsEnabled = nAddr > 0;
            }

            // Строки групп: флажок группы, стрелка раскрытия, флажки адресов
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var head = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var arrow = new Button { Padding = new Thickness(4, 0, 4, 0), Height = 26, Width = 26, Margin = new Thickness(0), ToolTip = "Показать адреса" };
                var glyph = new TextBlock { FontFamily = font, FontSize = 10, Text = "\uE76C", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                arrow.Content = glyph;
                int idx = gi;
                head.Children.Add(arrow);
                var gb = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                var label = Ui.Text(g.Name, 13, "SemiBold");
                gb.Content = label;
                head.Children.Add(gb);
                PkList.Children.Add(head);

                var kids = new StackPanel { Margin = new Thickness(58, 0, 0, 6), Visibility = Visibility.Collapsed };
                var boxes = new List<CheckBox>();
                foreach (var v in g.Values)
                {
                    var cb = new CheckBox { IsChecked = true, Margin = new Thickness(0, 2, 0, 2), Tag = v, Content = Ui.Text(Targets.FormatTargetLine(v), 13) };
                    cb.Click += (s, e) => UpdateCount();
                    kids.Children.Add(cb);
                    boxes.Add(cb);
                }
                PkList.Children.Add(kids);
                var row = new Row { Name = g.Name, GroupBox = gb, Label = label, Boxes = boxes, Kids = kids, Glyph = glyph };
                _rows.Add(row);

                // Частично отмеченная группа по щелчку отмечается целиком
                gb.Click += (s, e) =>
                {
                    bool on = gb.IsChecked == true;
                    foreach (var b in row.Boxes) b.IsChecked = on;
                    UpdateCount();
                };
                arrow.Click += (s, e) =>
                {
                    bool open = row.Kids.Visibility != Visibility.Visible;
                    row.Kids.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    row.Glyph.Text = open ? "\uE70D" : "\uE76C";
                    arrow.ToolTip = open ? "Скрыть адреса" : "Показать адреса";
                };
            }
            PkAll.Click += (s, e) => { foreach (var r in _rows) foreach (var b in r.Boxes) b.IsChecked = true; UpdateCount(); };
            PkNone.Click += (s, e) => { foreach (var r in _rows) foreach (var b in r.Boxes) b.IsChecked = false; UpdateCount(); };
            PkOk.Click += (s, e) => DialogResult = true;
            UpdateCount();
        }

        // Возвращает отмеченное в том же виде или null, если нажали «Отмена»
        public static List<PickerGroup> Show(Window owner, string title, string subtitle, List<PickerGroup> groups, string okText)
        {
            var d = new TargetsPickerDialog(title, subtitle, groups, okText);
            if (owner != null) d.Owner = owner;
            if (d.ShowDialog() != true) return null;
            var outp = new List<PickerGroup>();
            foreach (var r in d._rows)
            {
                var vals = r.Boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToArray();
                if (vals.Length > 0) outp.Add(new PickerGroup { Name = r.Name, Values = vals });
            }
            return outp;
        }
    }
}
