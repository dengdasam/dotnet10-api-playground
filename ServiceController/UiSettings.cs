using System.Text.Json;

namespace ServiceController;

/// <summary>
/// 窗口模式下的少量偏好设置。文件放在 logs/ 下（该目录已被 .gitignore 忽略），
/// 既不污染仓库，也避免把状态写进 Program Files 这类只读安装目录。
/// 两项：双击启动后是否自动打开浏览器调试页；点 × 关闭窗口时是否退出。
/// 全部由主窗口「设置」弹窗修改，旧配置文件缺字段时自动回落到默认值。
/// </summary>
internal sealed class UiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>启动时自动打开浏览器调试页；默认 false —— 有了主窗口就不再每次弹浏览器。</summary>
    public bool AutoOpenBrowser { get; set; }

    /// <summary>
    /// 点 × 关闭主窗口时的行为：false（默认）= 最小化到托盘、服务继续运行；
    /// true = 退出控制器并优雅停止主服务、释放端口。
    /// </summary>
    public bool ExitOnClose { get; set; }

    public static string FilePathOf(string root) => Path.Combine(root, "logs", "ui-settings.json");

    public static UiSettings Load(string root)
    {
        try
        {
            var path = FilePathOf(root);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings();
        }
        catch { /* 配置损坏或读不到就回落到默认值，不让启动失败 */ }
        return new UiSettings();
    }

    public void Save(string root)
    {
        try
        {
            var path = FilePathOf(root);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* 只读目录时忽略；本次会话内设置仍然生效 */ }
    }
}
