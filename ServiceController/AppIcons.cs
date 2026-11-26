using System.IO;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace ServiceController;

/// <summary>
/// 应用图标：图形取自开源图标集 Lucide 的 plug-zap（ISC，经 Iconify 获取）。
/// 源 SVG 与许可全文见 assets/icons/NOTICE.md，生成脚本 tools/make_icon.py。
///
/// 运行时只嵌一张白色线稿 glyph.png，再用 GDI+ 叠到纯色圆角底上：
/// 深靛 = exe / 窗口标题栏，绿 / 灰 / 红 = 托盘运行中 / 已停止 / 异常。
/// 三态与 exe 用同一个图形，不必为每个颜色再嵌一份资源；exe 文件本身
/// 由 csproj 的 ApplicationIcon 指向 assets/app.ico（多尺寸）。
/// </summary>
internal static class AppIcons
{
    /// <summary>设置弹窗「关于」里展示的图标署名。</summary>
    public const string Credit = "Lucide plug-zap · ISC · via Iconify";

    private static readonly List<IntPtr> Handles = [];
    private static readonly object Gate = new();
    private static Drawing.Bitmap? _glyph;

    private static Drawing.Bitmap Glyph
    {
        get
        {
            if (_glyph is not null) return _glyph;

            // 用 EmbeddedResource 而不是 WPF pack URI：纯托盘 / 命令行回退模式
            // 没有 Application 实例，也能读到同一份线稿。
            var asm = typeof(AppIcons).Assembly;
            var name = Array.Find(
                asm.GetManifestResourceNames(),
                n => n.EndsWith("glyph.png", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("缺少嵌入资源 assets/glyph.png");

            using var stream = asm.GetManifestResourceStream(name)!;
            using var loaded = Drawing.Image.FromStream(stream);
            _glyph = new Drawing.Bitmap(loaded);   // 复制一份，流释放后仍可用
            return _glyph;
        }
    }

    /// <summary>
    /// 生成 HICON：纯色圆角方块 + 居中的白色线稿。
    /// 托盘用 32 / 0.66 / 0.22（小尺寸要留边距，免得线条贴边）；
    /// 窗口与任务栏用 128 / 0.74 / 0.18——占比更满、圆角更小，和系统里其它应用图标一个视觉体量。
    /// </summary>
    public static Drawing.Icon MakeIcon(Drawing.Color color, int size = 32,
        float glyphRatio = 0.66f, float cornerRatio = 0.22f)
    {
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Drawing.Color.Transparent);

            var r = size * cornerRatio;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(size - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(size - r * 2, size - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, size - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new Drawing.SolidBrush(color);
            g.FillPath(brush, path);

            var glyph = Glyph;
            var side = (int)Math.Round(size * glyphRatio);
            var off = (size - side) / 2;
            g.DrawImage(glyph, new Drawing.Rectangle(off, off, side, side),
                0, 0, glyph.Width, glyph.Height, Drawing.GraphicsUnit.Pixel);
        }

        var handle = bmp.GetHicon();
        lock (Gate) Handles.Add(handle);
        return Drawing.Icon.FromHandle(handle);   // 句柄由 Dispose 统一销毁
    }

    /// <summary>退出前调用；两个外壳都调也没关系（幂等）。</summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            foreach (var handle in Handles)
            {
                try { DestroyIcon(handle); } catch { }
            }
            Handles.Clear();
        }
        _glyph?.Dispose();
        _glyph = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
