using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ServiceController;

/// <summary>
/// 设置弹窗（A 冷调文档感）：把主窗口里的设置项收进来，另有「关于」展示图标许可。
/// 只负责收集值，落盘与日志都由 <see cref="WindowShell"/> 统一处理。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly string _noticePath;

    public SettingsWindow(bool autoOpenBrowser, bool exitOnClose,
                          string iconCredit, string noticePath)
    {
        InitializeComponent();
        _noticePath = noticePath;

        ChkAutoOpen.IsChecked = autoOpenBrowser;
        CmbClose.SelectedIndex = exitOnClose ? 1 : 0;
        CreditText.Text = "应用图标：" + iconCredit;

        CmbClose.SelectionChanged += (_, _) => UpdateCloseHint();
        UpdateCloseHint();

        BtnSave.Click += (_, _) => { DialogResult = true; Close(); };
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnNotice.Click += (_, _) => OpenNotice();
    }

    /// <summary>弹窗关闭后读取：启动时自动打开页面。</summary>
    public bool AutoOpenBrowser => ChkAutoOpen.IsChecked == true;

    /// <summary>弹窗关闭后读取：点 × 时退出并停止服务（false = 最小化到托盘）。</summary>
    public bool ExitOnClose => CmbClose.SelectedIndex == 1;

    private void UpdateCloseHint() => CloseHint.Text = ExitOnClose
        ? "点 × 会优雅停止主服务并退出控制器，端口全部释放；托盘菜单「退出」行为不变。"
        : "点 × 只隐藏窗口，主服务继续运行；双击托盘图标或再次双击 exe 可唤回窗口。";

    private void OpenNotice()
    {
        try
        {
            if (!File.Exists(_noticePath)) { CloseHint.Text = "许可文件不存在：" + _noticePath; return; }
            Process.Start(new ProcessStartInfo(_noticePath) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            CloseHint.Text = "无法打开许可文件：" + ex.Message;
        }
    }
}
