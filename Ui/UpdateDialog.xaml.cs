using System.Diagnostics;
using System.Windows;

namespace ZapretUi
{
    public enum UpdateAction { None, OpenPage, Update }

    public partial class UpdateDialog : Window
    {
        public UpdateAction Action = UpdateAction.None;

        UpdateDialog(string local, string remote)
        {
            InitializeComponent();
            DialogBase.Init(this);
            if (Ctx.Main != null) Owner = Ctx.Main;
            DlgTitle.Text = $"Вышла новая версия zapret: {remote}";
            DlgText.Text = $"У вас установлена {local}.";
            DlgUpdate.Click += (s, e) => { Action = UpdateAction.Update; DialogResult = true; };
            DlgOpen.Click += (s, e) => { Action = UpdateAction.OpenPage; DialogResult = true; };
        }

        public static UpdateAction Show(string local, string remote)
        {
            var d = new UpdateDialog(local, remote);
            d.ShowDialog();
            if (d.DlgSkip.IsChecked == true)
            {
                Ctx.UiState.SkippedVersion = remote;
                Ctx.UiState.Save();
            }
            if (d.Action == UpdateAction.OpenPage)
                Process.Start(new ProcessStartInfo(Updater.DownloadUrl) { UseShellExecute = true });
            return d.Action;
        }
    }
}
