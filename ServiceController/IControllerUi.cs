namespace ServiceController;

/// <summary>
/// 控制器的「界面外壳」。同一套启停/编译逻辑要对接三种出口：
/// 命令行窗口（<see cref="ConsoleShell"/>）、托盘图标（<see cref="TrayShell"/>）
/// 与带主窗口的窗体外壳（<see cref="WindowShell"/>）。
/// </summary>
public interface IControllerUi
{
    /// <summary>给页面（/control/start|stop）用：把页面上的操作也写进当前日志出口，日志只有一个来源。</summary>
    void LogExternal(string text);

    /// <summary>控制器退出前的收尾：停掉定时器、让托盘图标消失、等待外壳线程结束。</summary>
    Task ShutdownAsync();

    /// <summary>重复双击 exe 时，把已有实例的主窗口调到前台；无窗口的外壳走默认空实现。</summary>
    void ShowWindow() { }

    /// <summary>当前外壳是否有可显示的主窗口，供 /control/show 判断要不要退化为提示。</summary>
    bool HasWindow => false;
}
