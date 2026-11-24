namespace ServiceController;

/// <summary>
/// 控制器的「界面外壳」。同一套启停/编译逻辑要对接两种出口：
/// 原来的命令行窗口（<see cref="ConsoleShell"/>）与托盘图标（<see cref="TrayShell"/>）。
/// </summary>
public interface IControllerUi
{
    /// <summary>给页面（/control/start|stop）用：把页面上的操作也写进当前日志出口，日志只有一个来源。</summary>
    void LogExternal(string text);

    /// <summary>控制器退出前的收尾：停掉定时器、让托盘图标消失、等待外壳线程结束。</summary>
    Task ShutdownAsync();
}
