using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ZapretUi
{
    public class TestSetup
    {
        public string Type;
        public string[] Names;
        public bool All;
        public bool Baseline;
    }

    public partial class TestSetupDialog : Window
    {
        readonly List<CheckBox> _boxes = new List<CheckBox>();

        TestSetupDialog()
        {
            InitializeComponent();
            DialogBase.Init(this);
            if (Ctx.Main != null) Owner = Ctx.Main;

            foreach (var s in Ctx.Strategies)
            {
                var cb = new CheckBox { IsChecked = true, Margin = new Thickness(0, 3, 0, 3), Tag = s.Name };
                string label = s.Name;
                if (Ctx.LatestResults != null)
                {
                    var prev = Ctx.LatestResults.Configs.FirstOrDefault(c => c.Name == s.Name);
                    if (prev != null) label += $"   ·   в прошлый раз {prev.Ok}/{prev.Total}";
                }
                cb.Content = label;
                SetupList.Children.Add(cb);
                _boxes.Add(cb);
            }

            string GetKind() => RbCombined.IsChecked == true ? "combined" : RbDpi.IsChecked == true ? "dpi" : "standard";

            void UpdateCount()
            {
                int n = _boxes.Count(b => b.IsChecked == true);
                TxtSelCount.Text = $"Выбрано {n} из {_boxes.Count}";
                TxtSelCount.Foreground = Theme.Pal["Muted"];
                int count = n + (ChkBaseline.IsChecked == true ? 1 : 0);
                var phases = TestRunner.GetTestPhases(GetKind());
                bool known = phases.All(p => Ctx.UiState.TestPace.ContainsKey(p));
                TxtEstimate.Text = count > 0
                    ? $"Займёт {TestRunner.FormatEstimate(TestRunner.GetTestEstimate(GetKind(), count))}" +
                      (known ? " — по прошлым тестам" : " — примерно, точнее будет после первого теста")
                    : "";
            }

            foreach (var cb in _boxes) cb.Click += (s, e) => UpdateCount();
            RbStandard.Checked += (s, e) => UpdateCount();
            RbDpi.Checked += (s, e) => UpdateCount();
            RbCombined.Checked += (s, e) => UpdateCount();
            ChkBaseline.Click += (s, e) => UpdateCount();
            BtnSelAll.Click += (s, e) => { foreach (var b in _boxes) b.IsChecked = true; UpdateCount(); };
            BtnSelNone.Click += (s, e) => { foreach (var b in _boxes) b.IsChecked = false; UpdateCount(); };

            ChkBaseline.IsChecked = Ctx.UiState.TestBaseline;
            if (Ctx.LatestResults != null)
            {
                var prev = Ctx.LatestResults.Configs.FirstOrDefault(c => c.Name == Ctx.BaselineName);
                if (prev != null) TxtBaseline.Text += $"   ·   в прошлый раз {prev.Ok}/{prev.Total}";
            }

            BtnGo.Click += (s, e) =>
            {
                // Можно проверить и одну строку «Без обхода» — без стратегий
                if (!_boxes.Any(b => b.IsChecked == true) && ChkBaseline.IsChecked != true)
                {
                    TxtSelCount.Text = "Выберите хотя бы одну стратегию";
                    TxtSelCount.Foreground = Theme.Pal["Bad"];
                    return;
                }
                DialogResult = true;
            };
            UpdateCount();
        }

        public static TestSetup Ask()
        {
            var d = new TestSetupDialog();
            if (d.ShowDialog() != true) return null;
            var names = d._boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToArray();
            bool baseline = d.ChkBaseline.IsChecked == true;
            if (Ctx.UiState.TestBaseline != baseline) { Ctx.UiState.TestBaseline = baseline; Ctx.UiState.Save(); }
            return new TestSetup
            {
                Type = d.RbCombined.IsChecked == true ? "combined" : d.RbDpi.IsChecked == true ? "dpi" : "standard",
                Names = names,
                All = names.Length == d._boxes.Count,
                Baseline = baseline,
            };
        }
    }
}
