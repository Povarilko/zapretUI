using System.Windows;
using System.Windows.Media;

namespace ZapretUi
{
    public partial class MessageDialog : Window
    {
        MessageDialog(string text, MessageKind kind, bool yesNo, string yes, string no)
        {
            InitializeComponent();
            DialogBase.Init(this);

            switch (kind)
            {
                case MessageKind.Question: MsgIcon.Text = "\uE9CE"; MsgIcon.Foreground = Theme.Pal["Accent"]; break;
                case MessageKind.Warning: MsgIcon.Text = "\uE7BA"; MsgIcon.Foreground = Theme.Pal["Mid"]; break;
                default: MsgIcon.Text = "\uE946"; MsgIcon.Foreground = Theme.Pal["Accent"]; break;
            }
            MsgText.Text = text;
            if (yesNo)
            {
                MsgYes.Content = yes;
                MsgNo.Content = no;
            }
            else
            {
                // Одна кнопка: и Enter, и Esc закрывают
                MsgYes.Content = "OK";
                MsgYes.IsCancel = true;
                MsgNo.Visibility = Visibility.Collapsed;
            }
            MsgYes.Click += (s, e) => { DialogResult = true; };
        }

        // Как MessageBox: Yes/No с -YesNo, иначе OK
        public static MessageResult Show(string text, MessageKind kind = MessageKind.Info,
            bool yesNo = false, string yes = "Да", string no = "Нет")
        {
            var d = new MessageDialog(text, kind, yesNo, yes, no);
            var owner = DialogBase.ActiveOwner();
            if (owner != null) d.Owner = owner;
            else { d.WindowStartupLocation = WindowStartupLocation.CenterScreen; d.ShowInTaskbar = true; }
            bool ok = d.ShowDialog() == true;
            if (!yesNo) return MessageResult.Ok;
            return ok ? MessageResult.Yes : MessageResult.No;
        }

        public static void ShowError(string msg) => Show(msg, MessageKind.Warning);
    }
}
