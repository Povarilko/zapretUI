using System.Windows;

namespace ZapretUi
{
    public partial class ConfirmServiceDialog : Window
    {
        public bool OkPressed;
        public bool DontAsk => DlgDontAsk.IsChecked == true;

        ConfirmServiceDialog(string title, string heading, string text, string hint, string okText)
        {
            InitializeComponent();
            DialogBase.Init(this);
            Title = title;
            DlgHeading.Text = heading;
            DlgText.Text = text;
            DlgHint.Text = hint;
            DlgOk.Content = okText;
            DlgOk.Click += (s, e) => { OkPressed = true; DialogResult = true; };
        }

        static (bool Ok, bool DontAsk) Show(Window owner, string title, string heading, string text, string hint, string okText)
        {
            var d = new ConfirmServiceDialog(title, heading, text, hint, okText);
            if (owner != null) d.Owner = owner;
            d.ShowDialog();
            return (d.OkPressed, d.DontAsk);
        }

        public static (bool Ok, bool DontAsk) ShowForTest(Window owner, string strategy)
        {
            string what = strategy != null ? $"со стратегией {strategy}" : "с прежней стратегией";
            return Show(owner, "Запуск теста", "Отключить службу zapret на время теста?",
                "Тест сам по очереди запускает все стратегии, а установленная служба ему мешает. Окно удалит службу на время теста и создаст её заново " + what + ", когда тест закончится.",
                "Тогда служба будет отключаться перед каждым тестом без вопроса. Вернуть вопрос можно на вкладке «Настройки».",
                "Отключить и запустить тест");
        }

        public static (bool Ok, bool DontAsk) ShowForStart(Window owner, string current, string name)
        {
            string cur = current != null ? $" со стратегией {current}" : "";
            return Show(owner, "Запуск стратегии", "Переустановить службу zapret с новой стратегией?",
                $"Сейчас установлена служба zapret{cur}. Окно удалит её и установит заново со стратегией {name} (как «Remove Services» и «Install Service» в service.bat).\n\nСлужба останется и будет запускаться вместе с Windows уже с новой стратегией.",
                "Тогда служба будет переустанавливаться при запуске стратегии без вопроса. Вернуть вопрос можно на вкладке «Настройки».",
                "Переустановить службу");
        }
    }
}
