using System.Text.Json;

namespace ServiceController;

/// <summary>
/// 窗口模式下的少量偏好设置。文件放在 logs/ 下（该目录已被 .gitignore 忽略），
/// 既不污染仓库，也避免把状态写进 Program Files 这类只读安装目录。
/// 只有一个开关：双击启动后是否自动打开浏览器调试页；默认 false —— 有了主窗口就不再每次弹浏览器。
/// </summary>
internal sealed class UiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool AutoOpenBrowser { get; set; }

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
