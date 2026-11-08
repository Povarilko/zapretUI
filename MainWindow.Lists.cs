using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZapretUi
{
    class ListInfo
    {
        public string File;
        public string Title;
        public bool Editable;
        public string Placeholder;
    }

    public partial class MainWindow
    {
        readonly ListInfo[] _listFiles =
        {
            new ListInfo { File = "list-general-user.txt", Title = "Свои домены (list-general-user)", Editable = true, Placeholder = "domain.example.abc" },
            new ListInfo { File = "list-exclude-user.txt", Title = "Свои исключения (list-exclude-user)", Editable = true, Placeholder = "domain.example.abc" },
            new ListInfo { File = "ipset-exclude-user.txt", Title = "Свои исключения IP (ipset-exclude-user)", Editable = true, Placeholder = Ctx.IpPlaceholder },
            new ListInfo { File = "list-general.txt", Title = "Базовые домены (list-general)", Editable = false },
            new ListInfo { File = "list-google.txt", Title = "Google и YouTube (list-google)", Editable = false },
            new ListInfo { File = "list-exclude.txt", Title = "Базовые исключения (list-exclude)", Editable = false },
            new ListInfo { File = "ipset-exclude.txt", Title = "Базовые исключения IP (ipset-exclude)", Editable = false },
        };

        string _listBaseline = "";
        int _listIndex = -1;

        void WireLists()
        {
            foreach (var l in _listFiles) CbList.Items.Add(l.Title);
            CbList.SelectionChanged += (s, e) => Safe.Run(() => { ConfirmListChanges(); LoadList(); });
            TbList.TextChanged += (s, e) => { UpdateListInfo(); UpdateListButtons(); };
            BtnSaveList.Click += (s, e) => Safe.Run(() => SaveList());
            BtnResetList.Click += (s, e) => Safe.Run(() => LoadList());
        }

        ListInfo CurrentList()
        {
            int i = CbList.SelectedIndex;
            return i >= 0 && i < _listFiles.Length ? _listFiles[i] : null;
        }

        void UpdateListInfo()
        {
            int n = System.Text.RegularExpressions.Regex.Split(TbList.Text, "\r?\n")
                .Count(l => l.Trim().Length > 0 && !l.Trim().StartsWith("#"));
            TxtListInfo.Text = $"{n} записей";
        }

        bool TestListDirty()
        {
            if (_listIndex < 0) return false;
            var l = _listFiles[_listIndex];
            return l.Editable && TbList.Text != _listBaseline;
        }

        // «Сохранить» и «Сбросить» доступны, только когда есть несохранённые изменения
        void UpdateListButtons()
        {
            bool dirty = TestListDirty();
            BtnSaveList.IsEnabled = dirty;
            BtnResetList.IsEnabled = dirty;
            BtnSaveList.Style = dirty ? (Style)FindResource("Primary") : (Style)FindResource(typeof(Button));
        }

        public void LoadList()
        {
            var l = CurrentList();
            if (l == null) return;
            _listIndex = CbList.SelectedIndex;
            string path = Path.Combine(Ctx.ListsDir, l.File);
            _listBaseline = File.Exists(path) ? File.ReadAllText(path).TrimEnd() : "";
            TbList.Text = _listBaseline;
            TbList.IsReadOnly = !l.Editable;
            TbList.Background = l.Editable ? Theme.Pal["Input"] : Theme.Pal["SurfaceAlt"];
            TxtListMsg.Text = l.Editable
                ? "По одному домену или адресу на строку, строки с # — комментарии. Поддомены подхватываются автоматически. Этот файл не перезаписывается при обновлении zapret."
                : "Базовый список zapret только для чтения: при обновлении он перезаписывается. Свои записи добавляйте в списки «Свои…».";
            UpdateListInfo();
            UpdateListButtons();
        }

        // Сохраняет текст поля в файл открытого списка
        void SaveList()
        {
            var l = CurrentList();
            if (l == null || !l.Editable) return;
            var lines = System.Text.RegularExpressions.Regex.Split(TbList.Text, "\r?\n")
                .Select(x => x.Trim()).ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (!lines.Any(x => x.Length > 0 && !x.StartsWith("#"))) lines.Add(l.Placeholder);
            Fs.WriteTextFile(Path.Combine(Ctx.ListsDir, l.File), lines.ToArray());
            // В поле — ровно то, что записано, чтобы изменений больше не было
            int caret = TbList.CaretIndex;
            _listBaseline = string.Join("\r\n", lines);
            TbList.Text = _listBaseline;
            TbList.CaretIndex = Math.Min(caret, TbList.Text.Length);
            // winws сам перечитывает списки, когда файл меняется, — перезапускать стратегию не нужно
            TxtListMsg.Text = $"Сохранено: {l.File}. Работающий обход подхватит изменения сам.";
            UpdateListInfo();
            UpdateListButtons();
        }

        // Перед сменой списка или закрытием окна: несохранённые правки не теряем молча
        void ConfirmListChanges()
        {
            if (!TestListDirty()) return;
            var l = _listFiles[_listIndex];
            var a = MessageDialog.Show($"В списке «{l.Title}» есть несохранённые изменения.", MessageKind.Question, true, "Сохранить", "Не сохранять");
            if (a == MessageResult.Yes) SaveList();
        }
    }
}
