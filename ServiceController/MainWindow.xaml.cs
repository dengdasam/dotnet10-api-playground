using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ServiceController;

/// <summary>
/// WPF 主窗口（A 冷调文档感）：只负责展示与事件转发，业务逻辑全在 <see cref="WindowShell"/>。
/// 配色沿用文档站：#F7F9FA 底 / 白卡片 / 深靛 #3B5A6A 主色 / #E2E7EB 细边框，无渐变无阴影。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(
        string pageUrl,
        string apiUrl,
        string logFile,
        Action onStart,
        Action onStop,
        Action onBuild,
        Action onOpenPage,
        Action onOpenApi,
        Action onOpenLog,
        Action onOpenFolder,
        Action onClear,
        Action onCopy,
        Action onOpenSettings)
    {
        InitializeComponent();

        PageLink.Text = "调试页  " + pageUrl;
        ApiLink.Text = "主服务  " + apiUrl;
        LogFileText.Text = "日志  " + Path.GetFileName(logFile);
        FooterText.Text = $"日志文件  {logFile}    ｜    主服务输出已精简（只留请求结果与告警）    ｜    右键托盘图标可退出";

        BtnStart.Click += (_, _) => onStart();
        BtnStop.Click += (_, _) => onStop();
        BtnBuild.Click += (_, _) => onBuild();
        BtnOpen.Click += (_, _) => onOpenPage();
        BtnLog.Click += (_, _) => onOpenLog();
        BtnFolder.Click += (_, _) => onOpenFolder();
        BtnClear.Click += (_, _) => onClear();
        BtnCopy.Click += (_, _) => onCopy();
        BtnSettings.Click += (_, _) => onOpenSettings();

        PageLink.MouseLeftButtonUp += (_, _) => onOpenPage();
        ApiLink.MouseLeftButtonUp += (_, _) => onOpenApi();
    }

    /// <summary>状态卡里的 × 行为说明随设置变化，避免提示与实际行为不一致。</summary>
    public void SetCloseHint(bool exitOnClose) => CloseHintText.Text = exitOnClose
        ? "× 关闭窗口 → 退出并停止服务、释放端口（可在「设置」中改回最小化到托盘）"
        : "× 关闭窗口 → 最小化到托盘，服务继续运行；托盘菜单「退出」才停止服务";

    /// <summary>刷新状态卡：圆点颜色、状态标题、副标题、窗口标题。</summary>
    public void SetStatus(string state, string meta, string dotHex)
    {
        StateText.Text = state;
        MetaText.Text = meta;
        StatusDot.Fill = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dotHex)!);
        Title = $"ApiPlayground · 服务控制器　[{state}]";
    }

    /// <summary>启停/编译期间禁用对应按钮，防连点。</summary>
    public void SetBusy(bool busy, bool running)
    {
        BtnStart.IsEnabled = !running && !busy;
        BtnStop.IsEnabled = running && !busy;
        BtnBuild.IsEnabled = !busy;
    }

    public void AppendLog(string text)
    {
        if (LogBox.Text.Length > 120_000)
            LogBox.Text = "…（窗口日志过长，已只保留最近内容）" + Environment.NewLine + LogBox.Text[^60_000..];
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    public void ClearLog() => LogBox.Clear();

    public string LogText => LogBox.Text;
}
