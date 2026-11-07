using System.Diagnostics;

namespace Tumbler;

/// <summary>
/// Запуск вместе с Windows. «Тумблеру» нужны права администратора, поэтому обычный ключ Run не годится:
/// используется задача планировщика «при входе в систему» с наивысшими правами — она стартует без окна UAC.
/// </summary>
internal static class Autostart
{
    const string TaskName = "Tumbler";

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool enabled)
    {
        if (!enabled) return Run($"/Delete /TN \"{TaskName}\" /F") == 0 || !IsEnabled();

        var command = $"\\\"{Environment.ProcessPath}\\\" --tray";
        return Run($"/Create /TN \"{TaskName}\" /TR \"{command}\" /SC ONLOGON /RL HIGHEST /F") == 0;
    }

    static int Run(string arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
