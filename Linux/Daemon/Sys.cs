using System.Diagnostics;

namespace Tumbler.Daemon;

/// <summary>Вызовы системных утилит и мелкие обёртки над libc.</summary>
internal static class Sys
{
    /// <summary>Реальный и действующий UID из /proc/self/status — без P/Invoke в libc, имя которой не везде одинаковое.</summary>
    static (uint Real, uint Effective) Ids()
    {
        if (!OperatingSystem.IsLinux()) return (1000, 1000);
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
            var fields = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            return (uint.Parse(fields[1]), uint.Parse(fields[2]));
        }
        return (uint.MaxValue, uint.MaxValue);
    }

    public static bool IsRoot => OperatingSystem.IsLinux() && Ids().Effective == 0;

    public static uint Uid => Ids().Real;

    /// <summary>Ищет утилиту в стандартных каталогах: у службы и у pkexec PATH урезан.</summary>
    public static string? Find(string tool) =>
        new[] { "/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/local/bin" }
            .Select(dir => Path.Combine(dir, tool))
            .FirstOrDefault(File.Exists);

    public static (int Code, string Output) Run(string tool, string arguments, string? stdin = null)
    {
        var path = Find(tool) ?? tool;
        var info = new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, (output + error.Result).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, $"не удалось запустить {tool}: {ex.Message}");
        }
    }

    /// <summary>Запуск, после которого ошибка должна попасть в журнал службы.</summary>
    public static bool Try(string tool, string arguments, string? stdin = null)
    {
        var (code, output) = Run(tool, arguments, stdin);
        if (code != 0) Log($"{tool} {arguments}: код {code}: {output}");
        return code == 0;
    }

    public static void Log(string text) => Console.WriteLine(text);

    /// <summary>Пишет одно значение в файл ядра (cgroupfs, /proc/sys). false — ядро отказало.</summary>
    public static bool WriteKernelFile(string path, string value)
    {
        try
        {
            File.WriteAllText(path, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
