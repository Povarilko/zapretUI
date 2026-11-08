using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ZapretUi
{
    public partial class MainWindow
    {
        static readonly int[] HistCols = { 120, 270, 120, 110, 120, 110 };
        static readonly Dictionary<string, string> SourceNames = new Dictionary<string, string>
        {
            ["ui"] = "из этого окна", ["manual"] = "вручную (.bat)", ["service"] = "служба",
        };

        void WireHistory()
        {
            BtnClearHistory.Click += (s, e) => Safe.Run(() =>
            {
                var r = MessageDialog.Show("Удалить всю историю запусков? Последняя стратегия останется.", MessageKind.Question, true, "Удалить", "Отмена");
                if (r != MessageResult.Yes) return;
                Ctx.UiState.History.Clear();
                Ctx.UiState.Save();
                ShowHistory();
            });
        }

        static string FormatDuration(TimeSpan d)
        {
            if (d.TotalMinutes < 1) return "меньше минуты";
            if (d.TotalHours < 1) return $"{(int)Math.Floor(d.TotalMinutes)} мин";
            if (d.TotalDays < 1) return $"{d.Hours} ч {d.Minutes} мин";
            return $"{(int)Math.Floor(d.TotalDays)} д {d.Hours} ч";
        }

        static Grid NewHistoryGrid()
        {
            var g = new Grid();
            foreach (int w in HistCols)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            return g;
        }

        public void ShowHistory()
        {
            var hdr = NewHistoryGrid();
            int i = 0;
            foreach (var h in new[] { "Когда", "Стратегия", "Как запущена", "Работала", "Последний тест" })
            {
                AddToGrid(hdr, Ui.Text(h, 12, "Normal", Theme.Pal["Muted"]), i);
                i++;
            }
            HistoryHeader.Content = hdr;
            HistoryRows.Children.Clear();

            var hst = Ctx.UiState.History;
            var starts = hst.Where(e => e.Event == "start").ToList();
            int distinct = starts.Select(e => e.Name).Distinct().Count();
            TxtHistoryInfo.Text = starts.Count > 0
                ? $"Запусков: {starts.Count}, разных стратегий: {distinct}. Записи во время тестов не попадают в историю."
                : "История пока пустая: она пополняется, пока открыто это окно. Записи во время тестов не попадают в историю.";

            for (int n = hst.Count - 1; n >= 0; n--)
            {
                var ev = hst[n];
                var row = NewHistoryGrid();
                var time = DateTime.Parse(ev.Time);
                AddToGrid(row, Ui.Text(time.ToString("dd.MM.yyyy HH:mm"), 12, "Normal", Theme.Pal["Muted"]), 0);

                if (ev.Event == "stop")
                {
                    AddToGrid(row, Ui.Text("обход остановлен", 13, "Normal", Theme.Pal["Muted"]), 1);
                }
                else
                {
                    bool isCurrent = n == hst.Count - 1 && Ctx.State != null && Ctx.State.Active == ev.Name;
                    var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
                    var nt = Ui.Text(ev.Name, 13, isCurrent ? "SemiBold" : "Normal");
                    nt.MaxWidth = HistCols[1] - 70;
                    nameStack.Children.Add(nt);
                    if (isCurrent) nameStack.Children.Add(Ui.Badge("сейчас", Theme.Pal["Sel"], Theme.Pal["SelText"]));
                    AddToGrid(row, nameStack, 1);

                    string src = ev.Source != null && SourceNames.TryGetValue(ev.Source, out var sn) ? sn : ev.Source;
                    AddToGrid(row, Ui.Text(src, 12, "Normal", Theme.Pal["Muted"]), 2);

                    DateTime? end = n < hst.Count - 1 ? DateTime.Parse(hst[n + 1].Time) : isCurrent ? DateTime.Now : (DateTime?)null;
                    string dur = end.HasValue ? FormatDuration(end.Value - time) : "—";
                    if (isCurrent) dur += " (идёт)";
                    AddToGrid(row, Ui.Text(dur, 12, "Normal", Theme.Pal["Muted"]), 3);

                    var score = Ctx.LatestResults?.Configs.FirstOrDefault(c => c.Name == ev.Name);
                    if (score != null)
                    {
                        var sc = Ui.RatioColors(score.Ok, score.Total);
                        var badge = Ui.Badge($"{score.Ok}/{score.Total}", sc.bg, sc.fg);
                        badge.HorizontalAlignment = HorizontalAlignment.Left;
                        badge.Margin = new Thickness(0);
                        AddToGrid(row, badge, 4);
                    }
                    else
                    {
                        AddToGrid(row, Ui.Text("—", 12, "Normal", Theme.Pal["Muted"]), 4);
                    }

                    if (!isCurrent && CbStrategy.Items.Contains(ev.Name))
                    {
                        var btn = new Button
                        {
                            Content = "Запустить",
                            Padding = new Thickness(8, 0, 8, 0),
                            Height = 28,
                            Tag = "\uE768",
                            Margin = new Thickness(0),
                            HorizontalAlignment = HorizontalAlignment.Left,
                        };
                        string name = ev.Name;
                        btn.Click += (s, e) => Safe.Run(() =>
                        {
                            CbStrategy.SelectedItem = name;
                            StartStrategy(name);
                        });
                        AddToGrid(row, btn, 5);
                    }
                }

                var b = new Border
                {
                    Padding = new Thickness(12, 6, 12, 6),
                    BorderBrush = Theme.Pal["Line"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = row,
                };
                HistoryRows.Children.Add(b);
            }
        }
    }
}
