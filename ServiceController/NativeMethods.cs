using System.Runtime.InteropServices;

namespace ServiceController;

/// <summary>
/// 早期错误（端口被占、启动失败）发生时窗口还没建起来，也不一定有 STA 消息泵，
/// 直接用纯 Win32 消息框，避免依赖 WinForms/WPF 的线程模型。
/// </summary>
internal static class NativeMethods
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbTopMost = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    internal static void Warn(string title, string text) =>
        MessageBoxW(IntPtr.Zero, text, title, MbOk | MbIconWarning | MbTopMost);
}
