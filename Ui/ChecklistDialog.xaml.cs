using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ZapretUi
{
    public partial class ChecklistDialog : Window
    {
        readonly List<CheckBox> _boxes = new List<CheckBox>();

        ChecklistDialog(string windowTitle, string title, string subtitle, List<MigrItem> items, string okText, string note)
        {
            InitializeComponent();
            DialogBase.Init(this);
            Title = windowTitle;
            DlgTitle.Text = title;
            DlgFrom.Text = subtitle;
            DlgNote.Text = note;
            DlgOk.Content = okText;
            foreach (var it in items)
            {
                var cb = new CheckBox { IsChecked = it.On, Tag = it.Id, Margin = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Top };
                var sp = new StackPanel();
                sp.Children.Add(Ui.Text(it.Title, 13));
                var det = Ui.Text(it.Detail, 12, "Normal", Theme.Pal["Muted"]);
                det.TextWrapping = TextWrapping.Wrap;
                det.TextTrimming = TextTrimming.None;
                det.MaxWidth = 500;
                sp.Children.Add(det);
                cb.Content = sp;
                Items.Children.Add(cb);
                _boxes.Add(cb);
            }
            DlgOk.Click += (s, e) => DialogResult = true;
        }

        // Возвращает Id отмеченных пунктов или null
        public static string[] Show(Window owner, string windowTitle, string title, string subtitle,
            List<MigrItem> items, string okText, string note)
        {
            var d = new ChecklistDialog(windowTitle, title, subtitle, items, okText, note);
            if (owner != null) d.Owner = owner;
            if (d.ShowDialog() != true) return null;
            var ids = new List<string>();
            foreach (var b in d._boxes)
                if (b.IsChecked == true) ids.Add((string)b.Tag);
            return ids.ToArray();
        }
    }
}
