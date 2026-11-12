using System.Diagnostics;
using System.Text;

namespace ServiceController;

/// <summary>
/// 编译主服务项目。首次启动（缺 dll）与窗口内 build 命令共用同一套逻辑。
/// </summary>
public static class ServiceBuild
{
    /// <summary>执行 dotnet build，返回是否成功、退出码与输出末尾若干行。</summary>
    public static async Task<(bool Ok, int ExitCode, string[] Tail)> BuildAsync(string projectDir, int tailLines = 8)
    {
        var csproj = Path.Combine(projectDir, "ApiPlayground.csproj");
        if (!File.Exists(csproj))
            return (false, -1, [$"找不到项目文件：{csproj}"]);

        var psi = new ProcessStartInfo("dotnet", $"build \"{csproj}\" -v minimal --nologo")
        {
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { return (false, -1, ["无法启动 dotnet build：" + ex.Message]); }
        if (p is null) return (false, -1, ["无法启动 dotnet build"]);

        var buffer = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (buffer) buffer.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (buffer) buffer.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();

        string[] tail;
        lock (buffer)
        {
            tail = buffer.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd())
                .TakeLast(tailLines)
                .ToArray();
        }
        return (p.ExitCode == 0, p.ExitCode, tail);
    }
}
