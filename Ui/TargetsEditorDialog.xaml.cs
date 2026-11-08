using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZapretUi
{
    public partial class TargetsEditorDialog : Window
    {
        TargetsModel _model;
        bool _loading;

        TargetsEditorDialog()
        {
            InitializeComponent();
            DialogBase.Init(this);
            if (Ctx.Main != null) Owner = Ctx.Main;
            BtnDefaults.Visibility = File.Exists(Ctx.TargetsDefault) ? Visibility.Visible : Visibility.Collapsed;

            _model = LoadModel(Targets.ReadTargetsModel(Ctx.TargetsTxt));

            LbGroups.SelectionChanged += (s, e) => { if (!_loading) { TxtErr.Text = ""; ShowGroup(); } };
            TbGroupName.TextChanged += (s, e) =>
            {
                if (_loading) return;
                int i = LbGroups.SelectedIndex;
                if (i < 0) return;
                _model.Groups[i].Name = TbGroupName.Text.Trim();
                ((TextBlock)LbGroups.Items[i]).Text = TbGroupName.Text.Trim();
            };
            TbAddrs.TextChanged += (s, e) =>
            {
                if (_loading) return;
                int i = LbGroups.SelectedIndex;
                if (i < 0) return;
                _model.Groups[i].Text = TbAddrs.Text;
                // Ошибка прошлой попытки сохранения уже может быть не про этот текст
                TxtErr.Text = "";
                CheckAddrs();
            };
            BtnAddGroup.Click += (s, e) =>
            {
                _model.Groups.Add(new TargetGroup { Name = "Новая группа" });
                FillGroups(_model.Groups.Count - 1);
                TbGroupName.Focus();
                TbGroupName.SelectAll();
            };
            BtnDelGroup.Click += (s, e) =>
            {
                int i = LbGroups.SelectedIndex;
                if (i < 0) return;
                var g = _model.Groups[i];
                int count = Targets.ReadTargetsText(g.Text).Values.Count;
                if (count > 0)
                {
                    var a = MessageDialog.Show($"Удалить группу «{g.Name}» и её адреса ({count})?", MessageKind.Question, true, "Удалить", "Отмена");
                    if (a != MessageResult.Yes) return;
                }
                _model.Groups.RemoveAt(i);
                FillGroups(i);
            };
            BtnUp.Click += (s, e) => Move(-1);
            BtnDown.Click += (s, e) => Move(1);
            BtnDefaults.Click += (s, e) =>
            {
                var a = MessageDialog.Show("Вернуть стандартный список целей zapret? Ваши изменения в форме пропадут.", MessageKind.Question, true, "Вернуть", "Отмена");
                if (a != MessageResult.Yes) return;
                _model = LoadModel(Targets.ReadTargetsModel(Ctx.TargetsDefault));
                FillGroups(0);
            };
            BtnExportTargets.Click += (s, e) => Safe.Run(ExportTargets);
            BtnImportTargets.Click += (s, e) => Safe.Run(ImportTargets);
            BtnSave.Click += (s, e) => Save();

            FillGroups(0);
        }

        static TargetsModel LoadModel(TargetsModel model)
        {
            foreach (var g in model.Groups) g.Text = Targets.LoadGroupText(g);
            return model;
        }

        void FillGroups(int select)
        {
            _loading = true;
            LbGroups.Items.Clear();
            foreach (var g in _model.Groups)
                LbGroups.Items.Add(Ui.Text(g.Name, 13));
            _loading = false;
            if (LbGroups.Items.Count > 0)
                LbGroups.SelectedIndex = Math.Max(0, Math.Min(select, LbGroups.Items.Count - 1));
            else ShowGroup();
        }

        // Проверка текста открытой группы: число адресов и строки, которые не похожи на адрес
        void CheckAddrs()
        {
            int i = LbGroups.SelectedIndex;
            if (i < 0) { TxtAddrCount.Text = ""; TxtAddrCheck.Text = ""; return; }
            var res = Targets.ReadTargetsText(_model.Groups[i].Text);
            int sites = res.Values.Count(v => !v.StartsWith("PING:", StringComparison.Ordinal));
            TxtAddrCount.Text = $"адресов: {res.Values.Count} · сайтов {sites}, пинг {res.Values.Count - sites}";
            if (res.Errors.Count > 0)
            {
                TxtAddrCheck.Foreground = Theme.Pal["Bad"];
                TxtAddrCheck.Text = "Не похоже на адрес — " + string.Join("; ", res.Errors) + ". Нужен сайт (example.com, https://example.com/…) или IP-адрес.";
            }
            else if (res.Duplicates.Count > 0)
            {
                TxtAddrCheck.Foreground = Theme.Pal["Warn"];
                TxtAddrCheck.Text = "Повторяются: " + string.Join(", ", res.Duplicates.Distinct()) + ". При сохранении останется по одному.";
            }
            else if (res.Values.Count == 0)
            {
                TxtAddrCheck.Foreground = Theme.Pal["Muted"];
                TxtAddrCheck.Text = "В группе пока нет адресов.";
            }
            else
            {
                TxtAddrCheck.Foreground = Theme.Pal["Good"];
                TxtAddrCheck.Text = "Все адреса в порядке.";
            }
        }

        void ShowGroup()
        {
            int i = LbGroups.SelectedIndex;
            bool has = i >= 0;
            GroupPane.IsEnabled = has;
            _loading = true;
            TbGroupName.Text = has ? _model.Groups[i].Name : "";
            TbAddrs.Text = has ? _model.Groups[i].Text : "";
            _loading = false;
            CheckAddrs();
        }

        void Move(int delta)
        {
            int i = LbGroups.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _model.Groups.Count) return;
            var g = _model.Groups[i];
            _model.Groups.RemoveAt(i);
            _model.Groups.Insert(j, g);
            FillGroups(j);
        }

        // Экспорт того, что сейчас в форме (вместе с несохранёнными правками); строки с ошибками пропускаются
        void ExportTargets()
        {
            var groups = _model.Groups
                .Select(g => (g.Name, res: Targets.ReadTargetsText(g.Text)))
                .Where(x => x.res.Values.Count > 0)
                .Select(x => new PickerGroup { Name = x.Name, Values = x.res.Values.ToArray() })
                .ToList();
            if (groups.Count == 0) { TxtErr.Text = "Нечего экспортировать: в группах нет адресов."; return; }
            string sub = "Отметьте группы целиком или раскройте группу и выберите отдельные адреса. Файл будет в формате targets.txt: его можно импортировать здесь или положить в папку utils вместо targets.txt.";
            if (_model.Groups.Any(g => Targets.ReadTargetsText(g.Text).Errors.Count > 0)) sub += " Строки с ошибками в экспорт не попадут.";
            var sel = TargetsPickerDialog.Show(this, "Экспорт настроек теста", sub, groups, "Экспортировать");
            if (sel == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт настроек теста",
                Filter = "Настройки теста (*.txt)|*.txt",
                FileName = "zapret-targets.txt",
            };
            if (dlg.ShowDialog(this) != true) return;
            // У адресов, которые уже были в targets.txt, — те же ключи
            var model = new TargetsModel();
            model.Header.Add("# targets.txt - endpoint list for zapret.ps1 tests (exported from zapret-ui)");
            int nAddr = 0;
            foreach (var s in sel)
            {
                var srcGroup = _model.Groups.FirstOrDefault(g => g.Name == s.Name);
                var keys = new Dictionary<string, string>();
                if (srcGroup != null)
                    foreach (var it in srcGroup.Items)
                        if (!string.IsNullOrEmpty(it.Key) && !keys.ContainsKey(it.Value)) keys[it.Value] = it.Key;
                var tg = new TargetGroup { Name = s.Name };
                foreach (var v in s.Values) { tg.Items.Add(new TargetItem { Key = keys.TryGetValue(v, out var k) ? k : null, Value = v }); nAddr++; }
                model.Groups.Add(tg);
            }
            Fs.WriteTextFile(dlg.FileName, Targets.ConvertToTargetsLines(model).ToArray());
            MessageDialog.Show($"Экспортировано адресов: {nAddr}, групп: {model.Groups.Count}.\n{dlg.FileName}");
        }

        // Импорт: файл настроек теста или простой список адресов. Записывается по «Сохранить»
        void ImportTargets()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импорт настроек теста",
                Filter = "Настройки теста или список адресов (*.txt)|*.txt|Все файлы|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            // Группы из всех файлов; одинаковые названия из разных файлов сливаются в одну группу без повторов
            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var empty = new List<string>();
            foreach (var file in dlg.FileNames)
            {
                var found = Targets.ReadTargetsModel(file).Groups
                    .Select(g => new PickerGroup
                    {
                        Name = g.Name,
                        Values = g.Items.Select(i => Targets.ConvertToTargetValue(i.Value)).Where(v => v != null).ToArray(),
                    })
                    .Where(x => x.Values.Length > 0)
                    .ToList();
                if (found.Count == 0)
                {
                    // Не файл настроек — читаем как список адресов, группа называется по имени файла
                    found.Add(new PickerGroup
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Values = Targets.ReadTargetsText(File.ReadAllText(file)).Values.ToArray(),
                    });
                }
                bool any = false;
                foreach (var g in found)
                {
                    if (!byName.TryGetValue(g.Name, out var list)) { list = new List<string>(); byName[g.Name] = list; }
                    foreach (var v in g.Values)
                        if (!list.Contains(v)) { list.Add(v); any = true; }
                }
                if (!any) empty.Add(Path.GetFileName(file));
            }
            var groups = byName.Where(kv => kv.Value.Count > 0)
                .Select(kv => new PickerGroup { Name = kv.Key, Values = kv.Value.ToArray() }).ToList();
            if (groups.Count == 0) throw new Exception("В выбранных файлах не нашлось адресов: " + string.Join(", ", empty) + ".");
            string sub = "Группы с такими же названиями, как у вас, дополнятся новыми адресами, остальные добавятся. В targets.txt всё запишется, когда нажмёте «Сохранить».";
            if (dlg.FileNames.Length > 1) sub = $"Файлов: {dlg.FileNames.Length}; группы с одинаковыми названиями из разных файлов объединены. " + sub;
            if (empty.Count > 0) sub += " Без новых адресов: " + string.Join(", ", empty) + ".";
            var sel = TargetsPickerDialog.Show(this, "Импорт настроек теста", sub, groups, "Импортировать");
            if (sel == null) return;
            int nAddr = 0, nNew = 0, first = -1;
            foreach (var s in sel)
            {
                int idx = _model.Groups.FindIndex(g => g.Name == s.Name);
                if (idx >= 0)
                {
                    var g = _model.Groups[idx];
                    var have = Targets.ReadTargetsText(g.Text).Values;
                    var news = s.Values.Where(v => !have.Contains(v)).ToArray();
                    if (news.Length > 0)
                    {
                        g.Text = (g.Text.TrimEnd() + "\r\n" + string.Join("\r\n", news.Select(Targets.FormatTargetLine))).TrimStart();
                        nAddr += news.Length;
                    }
                }
                else
                {
                    _model.Groups.Add(new TargetGroup
                    {
                        Name = s.Name,
                        Text = string.Join("\r\n", s.Values.Select(Targets.FormatTargetLine)),
                    });
                    idx = _model.Groups.Count - 1;
                    nAddr += s.Values.Length;
                    nNew++;
                }
                if (first < 0) first = idx;
            }
            FillGroups(first);
            string msg = nAddr > 0
                ? $"Добавлено адресов: {nAddr}, новых групп: {nNew}. Нажмите «Сохранить», чтобы записать в настройки теста."
                : "Все выбранные адреса уже есть в ваших группах.";
            MessageDialog.Show(msg);
        }

        void Save()
        {
            if (_model.Groups.Count == 0) { TxtErr.Text = "Нужна хотя бы одна группа."; return; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var parsed = new Dictionary<int, List<string>>();
            for (int i = 0; i < _model.Groups.Count; i++)
            {
                var g = _model.Groups[i];
                var res = Targets.ReadTargetsText(g.Text);
                string problem =
                    g.Name.Length == 0 ? "У группы нет названия." :
                    names.Contains(g.Name) ? $"Две группы называются «{g.Name}»." :
                    res.Errors.Count > 0 ? $"В группе «{g.Name}» есть строки, которые не похожи на адрес. Исправьте или удалите их." :
                    res.Values.Count == 0 ? $"В группе «{g.Name}» нет адресов. Добавьте адрес или удалите группу." : null;
                if (problem != null)
                {
                    // Открываем группу с ошибкой, чтобы её было видно
                    LbGroups.SelectedIndex = i;
                    TxtErr.Text = problem;
                    return;
                }
                names.Add(g.Name);
                parsed[i] = res.Values;
            }
            // Адреса из текста; у прежних адресов сохраняем их ключи в targets.txt
            for (int i = 0; i < _model.Groups.Count; i++)
            {
                var g = _model.Groups[i];
                var oldKeys = new Dictionary<string, string>();
                foreach (var it in g.Items)
                    if (!string.IsNullOrEmpty(it.Key) && !oldKeys.ContainsKey(it.Value)) oldKeys[it.Value] = it.Key;
                var items = new List<TargetItem>();
                foreach (var v in parsed[i])
                    items.Add(new TargetItem { Key = oldKeys.TryGetValue(v, out var k) ? k : null, Value = v });
                g.Items = items;
            }
            try { Targets.SaveTargetsModel(_model, Ctx.TargetsTxt); }
            catch (Exception ex) { TxtErr.Text = "Не удалось сохранить: " + ex.Message; return; }
            DialogResult = true;
        }

        public static bool ShowEditor()
        {
            var d = new TargetsEditorDialog();
            return d.ShowDialog() == true;
        }
    }
}
