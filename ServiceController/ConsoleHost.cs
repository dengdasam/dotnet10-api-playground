using System.Runtime.InteropServices;
using System.Text;

namespace ServiceController;

/// <summary>
/// WinExe 子系统本身没有控制台，所有 Console.WriteLine 会静默丢进 Stream.Null。
/// 用 --console 启动时在这里临时挂一个：能复用调用者的 cmd 窗口就复用，否则新建一个，
/// 然后把 Console.Out/Error/In 重新指向它，命令行模式的行为才和以前完全一致。
/// </summary>
internal static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    /// <summary>确保有一个可写的中文控制台。已经有控制台（比如 dotnet 宿主或终端提供）时直接返回。</summary>
    public static bool TryAttach()
    {
        // 注意：不能用 Console.Out 判断——无控制台时它指向 Stream.Null，写不报错也看不到内容
        if (GetConsoleWindow() != IntPtr.Zero) return true;

        // 从 cmd 里执行时复用父进程窗口；双击执行时新建一个
        if (!AttachConsole(AttachParentProcess) && !AllocConsole()) return false;

        try
        {
            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            if (!Console.IsInputRedirected)
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        }
        catch { /* 流接管失败仍可用，只是可能看不到输出 */ }

        try { Console.Title = "ApiPlayground 控制器（--console）"; } catch { }
        return true;
    }
}
