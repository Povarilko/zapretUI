using System.Windows;

namespace ZapretUi
{
    public enum MessageKind { Info, Question, Warning }

    public enum MessageResult { Ok, Yes, No }

    // Общее для окон-диалогов: иконка и тёмный заголовок
    public static class DialogBase
    {
        public static void Init(Window w)
        {
            if (Ctx.AppIcon != null) w.Icon = Ctx.AppIcon;
            w.SourceInitialized += (s, e) => Theme.SetTitleBar(w);
        }

        // Владелец — активный диалог (редактор теста и т. п.), иначе главное окно
        public static Window ActiveOwner()
        {
            if (Ctx.Main != null && Ctx.Main.IsVisible)
            {
                foreach (Window w in Ctx.Main.OwnedWindows)
                    if (w.IsActive) return w;
                return Ctx.Main;
            }
            return null;
        }
    }
}
