using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZapretUi
{
    public partial class MainWindow
    {
        const int ColName = 175, ColScore = 130, ColGroup = 72, ColPing = 72;

        void WireResults()
        {
            ResultsScroll.ScrollChanged += (s, e) =>
                ResultsHeaderScroll.ScrollToHorizontalOffset(ResultsScroll.HorizontalOffset);
            CbResults.SelectionChanged += (s, e) => Safe.Run(() =>
            {
                if (Ctx.Loading || CbResults.SelectedIndex < 0) return;
                Ctx.Results = ResultsModel.ReadTestResults(Ctx.ResultFiles[CbResults.SelectedIndex].FullName);
                ShowResults();
                RestoreDetailPanel();
            });
        }

        // "general (ALT3)" -> "ALT3", чтобы в таблице было видно отличающуюся часть
        static string GetShortName(string name)
        {
            if (name == Ctx.BaselineName) return "Без обхода";
            var m = System.Text.RegularExpressions.Regex.Match(name, "^general \\((.+)\\)$");
            return m.Success ? m.Groups[1].Value : name;
        }

        static string GetDisplayName(string name) => name == Ctx.BaselineName ? "Без обхода" : name;

        // Панель подробностей: ширина рамки анимируется, таблица слева сжимается/расширяется вместе с ней
        void SetDetailOpen(bool open, bool remember = true)
        {
            Ctx.DetailOpen = open;
            var anim = new DoubleAnimation
            {
                To = open ? 362 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            DetailPanel.BeginAnimation(WidthProperty, anim);
            if (remember && Ctx.UiState.DetailOpen != open) { Ctx.UiState.DetailOpen = open; Ctx.UiState.Save(); }
        }

        // Панель была открыта — открыть её снова с активной стратегией (или лучшей)
        void RestoreDetailPanel()
        {
            if (!Ctx.UiState.DetailOpen || Ctx.Results == null || Ctx.Results.Configs.Count == 0) return;
            if (Ctx.Run != null && !Ctx.Run.Finished) return;
            if (Ctx.DetailOpen && Ctx.SelectedResult != null) return;
            string want = Ctx.State?.Active ?? Ctx.Results.Best;
            var cfg = Ctx.Results.Configs.FirstOrDefault(c => c.Name == want) ?? Ctx.Results.Configs.FirstOrDefault();
            if (cfg != null) SelectResult(cfg);
        }

        public void CloseDetail()
        {
            Ctx.SelectedResult = null;
            foreach (Border b in ResultsRows.Children) b.Background = Theme.Pal["White"];
            SetDetailOpen(false);
        }

        static Grid NewRowGrid(int groupCount)
        {
            var g = new Grid();
            foreach (int w in new[] { ColName, ColScore }.Concat(Enumerable.Repeat(ColGroup, groupCount)).Concat(new[] { ColPing }))
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            return g;
        }

        static void AddToGrid(Grid grid, UIElement el, int col)
        {
            Grid.SetColumn(el, col);
            grid.Children.Add(el);
        }

        public void LoadResultFiles()
        {
            Ctx.Loading = true;
            Ctx.ResultFiles = ResultsModel.GetResultFiles();
            CbResults.Items.Clear();
            foreach (var f in Ctx.ResultFiles) CbResults.Items.Add(ResultsModel.FormatResultFileName(f));
            Ctx.Loading = false;
            if (Ctx.ResultFiles.Length > 0)
            {
                Ctx.LatestResults = ResultsModel.ReadTestResults(Ctx.ResultFiles[0].FullName);
                CbResults.SelectedIndex = 0;
            }
            else
            {
                Ctx.LatestResults = null;
                ResultsRows.Children.Clear();
                ResultsHeader.Content = Ui.Text("Тестов ещё не было. Нажмите «Запустить тест…».", 13, "Normal", Theme.Pal["Muted"]);
            }
        }

        public void ShowResults()
        {
            var r = Ctx.Results;
            ResultsRows.Children.Clear();
            if (r == null) return;

            var groups = r.Groups;
            var hdr = NewRowGrid(groups.Count);
            AddToGrid(hdr, Ui.Text("Стратегия", 12, "Normal", Theme.Pal["Muted"]), 0);
            AddToGrid(hdr, Ui.Text("Всего проверок", 12, "Normal", Theme.Pal["Muted"]), 1);
            for (int i = 0; i < groups.Count; i++) AddToGrid(hdr, Ui.Text(groups[i], 12, "Normal", Theme.Pal["Muted"]), 2 + i);
            AddToGrid(hdr, Ui.Text("Пинг", 12, "Normal", Theme.Pal["Muted"]), 2 + groups.Count);
            ResultsHeader.Content = hdr;

            string active = Ctx.State != null && !Ctx.State.Testing ? Ctx.State.Active : null;
            foreach (var c in r.Configs)
            {
                var row = NewRowGrid(groups.Count);

                var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
                string weight = c.Name == active ? "SemiBold" : "Normal";
                bool isBaseline = c.Name == Ctx.BaselineName;
                var nameText = Ui.Text(GetShortName(c.Name), 13, weight, isBaseline ? Theme.Pal["Muted"] : null);
                nameText.ToolTip = isBaseline ? "Те же проверки с выключенным zapret — для сравнения со стратегиями" : c.Name;
                nameText.MaxWidth = (c.Pending || c.Failed || c.Name == active || c.Name == r.Best) ? ColName - 66 : ColName - 10;
                nameStack.Children.Add(nameText);
                if (c.Pending) nameStack.Children.Add(Ui.Badge("проверяется", Theme.Pal["MidBg"], Theme.Pal["MidFg"]));
                else if (c.Failed) nameStack.Children.Add(Ui.Badge("не запустилась", Theme.Pal["BadBg"], Theme.Pal["BadFg"]));
                else if (c.Name == active) nameStack.Children.Add(Ui.Badge("активна", Theme.Pal["Sel"], Theme.Pal["SelText"]));
                else if (c.Name == r.Best) nameStack.Children.Add(Ui.Badge("лучшая", Theme.Pal["GoodBg"], Theme.Pal["GoodFg"]));
                AddToGrid(row, nameStack, 0);

                var cols = Ui.RatioColors(c.Ok, c.Total);
                var scoreStack = new Grid { Margin = new Thickness(0, 0, 14, 0) };
                scoreStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                scoreStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Pal["Track"], VerticalAlignment = VerticalAlignment.Center };
                var fill = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = cols.solid,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = c.Total > 0 ? Math.Max(2, (ColScore - 66) * c.Ok / (double)c.Total) : 0,
                };
                var barHost = new Grid();
                barHost.Children.Add(track);
                barHost.Children.Add(fill);
                AddToGrid(scoreStack, barHost, 0);
                var st = Ui.Text($"{c.Ok}/{c.Total}", 12, "Normal", Theme.Pal["Muted"]);
                st.HorizontalAlignment = HorizontalAlignment.Right;
                AddToGrid(scoreStack, st, 1);
                AddToGrid(row, scoreStack, 1);

                for (int i = 0; i < groups.Count; i++)
                {
                    int ok = 0, tot = 0;
                    foreach (var t in c.Targets)
                    {
                        if (t.Group != groups[i]) continue;
                        foreach (var tok in t.Tokens)
                        {
                            tot++;
                            if (tok.Code == "OK") ok++;
                        }
                    }
                    var gc = Ui.RatioColors(ok, tot);
                    var cell = new Border
                    {
                        Background = gc.bg,
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 8, 0),
                        Padding = new Thickness(0, 2, 0, 2),
                        Child = Ui.Text(tot > 0 ? $"{ok}/{tot}" : "—", 12, "Normal", gc.fg),
                    };
                    ((TextBlock)cell.Child).HorizontalAlignment = HorizontalAlignment.Center;
                    AddToGrid(row, cell, 2 + i);
                }

                string pingText = c.PingAvg.HasValue ? $"{c.PingAvg} мс" : "—";
                if (c.PingFail > 0) pingText += $" · {c.PingFail}×";
                AddToGrid(row, Ui.Text(pingText, 12, "Normal", Theme.Pal["Muted"]), 2 + groups.Count);

                var b = new Border
                {
                    Padding = new Thickness(12, 7, 12, 7),
                    BorderBrush = Theme.Pal["Line"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Cursor = Cursors.Hand,
                    Background = Ctx.SelectedResult == c.Name ? Theme.Pal["Sel"] : Theme.Pal["White"],
                    Tag = c,
                    Child = row,
                };
                b.MouseLeftButtonUp += (s, e) => SelectResult((Config)((Border)s).Tag);
                ResultsRows.Children.Add(b);
            }

            if (Ctx.SelectedResult != null)
            {
                var sel = r.Configs.FirstOrDefault(c => c.Name == Ctx.SelectedResult);
                if (sel != null) ShowDetail(sel);
                else ClearDetail();
            }
        }

        // Нажатие на строку: открыть панель с этой стратегией; повторное нажатие на ту же строку — свернуть
        public void SelectResult(Config cfg)
        {
            if (Ctx.DetailOpen && Ctx.SelectedResult == cfg.Name) { CloseDetail(); return; }
            Ctx.SelectedResult = cfg.Name;
            foreach (Border b in ResultsRows.Children)
                b.Background = ((Config)b.Tag).Name == cfg.Name ? Theme.Pal["Sel"] : Theme.Pal["White"];
            ShowDetail(cfg);
            if (!Ctx.DetailOpen) SetDetailOpen(true);
        }

        public void ClearDetail()
        {
            DetailTitle.Text = "Нажмите на строку стратегии";
            DetailSub.Text = "";
            DetailRows.Children.Clear();
            BtnStartSelected.Visibility = Visibility.Collapsed;
            // Пустую панель открытой не держим; запомненное «панель открыта» не трогаем
            Ctx.SelectedResult = null;
            if (Ctx.DetailOpen) SetDetailOpen(false, false);
        }

        UIElement NewDetailLine(Target t)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var ping = Ui.Text(t.Ping != null ? t.Ping.Replace(" ms", " мс") : "", 12, "Normal", Theme.Pal["Muted"]);
            DockPanel.SetDock(ping, Dock.Right);
            line.Children.Add(ping);
            var toks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(toks, Dock.Right);
            foreach (var tok in t.Tokens)
            {
                bool ok = tok.Code == "OK";
                var bg = ok ? Theme.Pal["GoodBg"] : (tok.Code.Contains("UNSUP") ? Theme.Pal["NaBg"] : Theme.Pal["BadBg"]);
                var fg = ok ? Theme.Pal["GoodFg"] : (tok.Code.Contains("UNSUP") ? Theme.Pal["NaFg"] : Theme.Pal["BadFg"]);
                string label = tok.Label.StartsWith("TLS", StringComparison.Ordinal) ? tok.Label.Substring(3) : tok.Label;
                var badge = Ui.Badge(label, bg, fg);
                string code = tok.Code;
                badge.ToolTip = code == "OK" ? "проходит"
                    : code == "LIKELY_BLOCKED" ? "обрыв на 16–20 КБ — DPI режет соединение"
                    : code.Contains("UNSUP") ? "не поддерживается"
                    : $"ошибка ({code})";
                toks.Children.Add(badge);
            }
            line.Children.Add(toks);
            var key = Ui.Text(t.Key, 12);
            key.ToolTip = t.Key;
            line.Children.Add(key);
            return line;
        }

        void ShowDetail(Config cfg)
        {
            DetailTitle.Text = GetDisplayName(cfg.Name);
            DetailSub.Text = $"прошло {cfg.Ok} из {cfg.Total} проверок · тест {ResultsModel.TestTypeName(cfg.Type)}";
            DetailRows.Children.Clear();

            string lastGroup = null;
            var dpi = new List<Target>();
            foreach (var t in cfg.Targets)
            {
                // Цели DPI — отдельным сворачиваемым блоком ниже
                if (t.Group == "DPI") { dpi.Add(t); continue; }
                if (t.Group != lastGroup)
                {
                    var gh = Ui.Text(t.Group, 12, "SemiBold", Theme.Pal["Muted"]);
                    gh.Margin = new Thickness(0, 8, 0, 2);
                    DetailRows.Children.Add(gh);
                    lastGroup = t.Group;
                }
                DetailRows.Children.Add(NewDetailLine(t));
            }

            // Тест только DPI: сразу список хостингов, без раздела
            if (dpi.Count > 0 && dpi.Count == cfg.Targets.Count)
            {
                int cut = dpi.SelectMany(t => t.Tokens).Count(tok => tok.Code == "LIKELY_BLOCKED");
                // Неразрывные пробелы: при переносе фраза уходит на новую строку целиком
                string suffix = (" · обрыв 16–20 КБ: " + (cut > 0 ? cut.ToString() : "нет"))
                    .Replace(' ', '\u00A0').Replace("\u00A0·", " ·").Replace('–', '\u2011');
                DetailSub.Text += suffix;
                foreach (var t in dpi) DetailRows.Children.Add(NewDetailLine(t));
            }
            else if (dpi.Count > 0)
            {
                int ok = 0, tot = 0, cut = 0;
                foreach (var tok in dpi.SelectMany(t => t.Tokens))
                {
                    tot++;
                    if (tok.Code == "OK") ok++;
                    else if (tok.Code == "LIKELY_BLOCKED") cut++;
                }
                var cols = Ui.RatioColors(ok, tot);
                // Заголовок блока — строка, по щелчку раскрывается
                var head = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, 8, 0, 2) };
                var hp = new DockPanel();
                var sum = Ui.Badge($"{ok}/{tot}", cols.bg, cols.fg);
                DockPanel.SetDock(sum, Dock.Right);
                hp.Children.Add(sum);
                var arrow = new TextBlock
                {
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                };
                hp.Children.Add(arrow);
                var title = Ui.Text("DPI · обрыв 16–20 КБ" + (cut > 0 ? $": {cut}" : ": нет"), 12, "SemiBold", Theme.Pal["Muted"]);
                hp.Children.Add(title);
                head.Child = hp;
                var body = new StackPanel();
                foreach (var t in dpi) body.Children.Add(NewDetailLine(t));
                SetDetailDpiState(body, arrow, open: false);
                head.MouseLeftButtonUp += (s, e) =>
                {
                    Ctx.DetailDpiOpen = !Ctx.DetailDpiOpen;
                    SetDetailDpiState(body, arrow, Ctx.DetailDpiOpen);
                };
                DetailRows.Children.Add(head);
                DetailRows.Children.Add(body);
            }
            BtnStartSelected.Visibility = CbStrategy.Items.Contains(cfg.Name) ? Visibility.Visible : Visibility.Collapsed;
        }

        static void SetDetailDpiState(StackPanel body, TextBlock arrow, bool open)
        {
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "\uE70D" : "\uE76C";
            body.ToolTip = open ? "Свернуть проверки DPI" : "Показать проверки DPI по каждому хостингу";
        }
    }
}
