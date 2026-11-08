using System;
using System.Runtime.InteropServices;

namespace ZapretUi
{
    // WinAPI-обёртки: тёмный заголовок окна и своё имя приложения для панели задач
    internal static class Native
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
