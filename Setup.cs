using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Tumbler;

internal sealed class SetupException(string message) : Exception(message);

/// <summary>Строка мастера установки: компонент и стоит ли он уже.</summary>
public sealed record SetupItem(string Title, string Detail, bool Installed);

/// <summary>
/// Установка и удаление того, без чего «Тумблер» не работает: драйвер Windows Packet Filter, служба ProxiFyre,
/// среда Visual C++ и правило брандмауэра. Драйвер и ProxiFyre лежат внутри exe как ресурсы — это неизменённые
/// официальные сборки, их контрольные суммы сверяются перед установкой.
/// </summary>
internal static class Setup
{
    public const string DriverVersion = "3.6.2";
    public const string ProxiFyreVersion = "2.6.3";

    const string DriverResource = "Payload.Driver.msi";
    const string DriverSha256 = "9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c";
    const string ProxiFyreResource = "Payload.ProxiFyre.zip";
    const string ProxiFyreSha256 = "f4e4a2963404ca61201ddba4f93677a6b58809d29874951e5c0f93030a9cc9ae";

    const string DriverService = "ndisrd";
    const string FirewallRuleName = "ProxiFyre (Tumbler)";
    const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    // Библиотеки, которые импортирует socksify.dll из состава ProxiFyre.
    static readonly string[] VcRuntimeFiles = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_atomic_wait.dll"];

    static readonly object LogLock = new();

    public static string InstallDir =>
        ServiceCtl.InstallDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProxiFyre");

    public static string LogFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tumbler", "setup.log");

    static string ProxiFyreExe => Path.Combine(InstallDir, "ProxiFyre.exe");
    static bool DriverInstalled => ServiceCtl.Query(DriverService) != ServiceState.NotInstalled;
    static bool VcRuntimeInstalled => VcRuntimeFiles.All(f => File.Exists(Path.Combine(Environment.SystemDirectory, f)));
    static bool FilesInstalled => File.Exists(ProxiFyreExe) && File.Exists(Path.Combine(InstallDir, "socksify.dll"));
    static bool ServiceInstalled => ServiceCtl.Query() != ServiceState.NotInstalled;

    /// <summary>Быстрая проверка при запуске: всё ли на месте, или нужен мастер установки.</summary>
    public static bool IsComplete => DriverInstalled && VcRuntimeInstalled && FilesInstalled && ServiceInstalled;

    public static List<SetupItem> Check()
    {
        var items = new List<SetupItem>
        {
            new($"Драйвер Windows Packet Filter {DriverVersion}",
                "Перехватывает сетевые пакеты выбранных приложений. Официальный установщик с цифровой подписью разработчика.",
                DriverInstalled),
            new($"ProxiFyre {ProxiFyreVersion}",
                $"Служба Windows, которая отправляет перехваченные пакеты в твой прокси. Ставится в {InstallDir}.",
                FilesInstalled && ServiceInstalled),
            new("Правило брандмауэра Windows",
                "Разрешает входящие соединения для ProxiFyre.exe — без него перехваченные соединения блокируются.",
                FilesInstalled && Diagnostics.FirewallAllows(ProxiFyreExe) == true),
        };
        // Среда Visual C++ обычно уже стоит — показываем её, только когда придётся качать.
        if (!VcRuntimeInstalled)
            items.Insert(1, new("Microsoft Visual C++ Runtime",
                "Нужна для ProxiFyre. Будет скачана с сайта Microsoft (около 25 МБ) — потребуется интернет.", false));
        return items;
    }

    /// <returns>true, если драйвер заработает только после перезагрузки.</returns>
    public static bool Install(Action<string> progress)
    {
        Log($"=== Установка, Тумблер {Assembly.GetExecutingAssembly().GetName().Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new SetupException("Поддерживается только 64-битная Windows на процессорах Intel и AMD.");

        var temp = Directory.CreateTempSubdirectory("tumbler-setup-").FullName;
        bool reboot = false;
        try
        {
            if (!DriverInstalled)
            {
                progress("Устанавливаю драйвер Windows Packet Filter…");
                reboot |= InstallDriver(temp);
            }
            if (!VcRuntimeInstalled)
            {
                progress("Скачиваю Microsoft Visual C++ Runtime…");
                reboot |= InstallVcRuntime(temp, progress);
            }
            if (!FilesInstalled)
            {
                progress("Распаковываю ProxiFyre…");
                InstallFiles(temp);
            }
            if (!ServiceInstalled)
            {
                progress("Регистрирую службу ProxiFyre…");
                InstallService();
            }
            progress("Настраиваю брандмауэр…");
            EnsureFirewallRule();
        }
        finally
        {
            TryDelete(temp);
        }

        Log(reboot ? "Готово, нужна перезагрузка." : "Готово.");
        return reboot;
    }

    public static void Uninstall(bool removeDriver, Action<string> progress)
    {
        Log($"=== Удаление, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        // Папку запоминаем заранее: после удаления службы её путь из реестра уже не узнать.
        var dir = InstallDir;
        var exe = Path.Combine(dir, "ProxiFyre.exe");

        if (ServiceInstalled)
        {
            progress("Останавливаю службу ProxiFyre…");
            try
            {
                ServiceCtl.Stop();
            }
            catch (Exception ex) when (ex is Win32Exception or TimeoutException)
            {
                Log($"Служба не остановилась штатно: {ex.Message}");
            }
            if (File.Exists(exe)) Run(exe, "uninstall", dir);
        }

        progress("Удаляю правило брандмауэра…");
        Run("netsh.exe", $"advfirewall firewall delete rule name=all dir=in program=\"{exe}\"");
        Autostart.Set(false);

        progress("Удаляю файлы ProxiFyre…");
        if (File.Exists(exe) && Path.GetFileName(dir).Contains("ProxiFyre", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(dir, recursive: true);

        if (removeDriver)
        {
            progress("Удаляю драйвер Windows Packet Filter…");
            foreach (var productCode in DriverProductCodes())
                Run("msiexec.exe", $"/x {productCode} /qn /norestart");
        }
        Log("Удалено.");
    }

    /// <summary>Распаковывает встроенные установщики в папку, сверяя контрольные суммы. Нужно для самопроверки сборки.</summary>
    public static (string DriverMsi, string ProxiFyreZip) ExtractPayload(string dir) => (
        ExtractResource(DriverResource, DriverSha256, Path.Combine(dir, "WindowsPacketFilter.msi")),
        ExtractResource(ProxiFyreResource, ProxiFyreSha256, Path.Combine(dir, "ProxiFyre.zip")));

    // ---------- Шаги установки ----------

    static bool InstallDriver(string temp)
    {
        var msi = ExtractResource(DriverResource, DriverSha256, Path.Combine(temp, "WindowsPacketFilter.msi"));
        int code = Run("msiexec.exe", $"/i \"{msi}\" /qn /norestart");
        if (code is not (0 or 3010))
            throw new SetupException($"Установщик драйвера завершился с кодом {code}.");

        for (int i = 0; i < 40 && ServiceCtl.Query(DriverService) != ServiceState.Running; i++)
            Thread.Sleep(250);
        return code == 3010 || ServiceCtl.Query(DriverService) != ServiceState.Running;
    }

    static bool InstallVcRuntime(string temp, Action<string> progress)
    {
        const string manual = $"Установи его вручную со страницы {VcRedistUrl} и запусти «Тумблер» снова.";
        var installer = Path.Combine(temp, "vc_redist.x64.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = http.GetAsync(VcRedistUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var file = File.Create(installer);
            response.Content.CopyToAsync(file).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log($"Не скачался {VcRedistUrl}: {ex.Message}");
            throw new SetupException("Не удалось скачать Microsoft Visual C++ Runtime. " + manual);
        }

        // Скачанное из интернета запускаем, только если это действительно подписано Microsoft.
        if (!Trust.IsSignedBy(installer, "Microsoft Corporation"))
            throw new SetupException("Скачанный установщик Visual C++ Runtime не прошёл проверку подписи Microsoft. " + manual);

        progress("Устанавливаю Microsoft Visual C++ Runtime…");
        int code = Run(installer, "/install /quiet /norestart");
        // 1638 — уже стоит более новая версия.
        if (code is not (0 or 3010 or 1638))
            throw new SetupException($"Установщик Visual C++ Runtime завершился с кодом {code}. " + manual);
        return code == 3010;
    }

    static void InstallFiles(string temp)
    {
        var dir = InstallDir;
        var zip = ExtractResource(ProxiFyreResource, ProxiFyreSha256, Path.Combine(temp, "ProxiFyre.zip"));
        Directory.CreateDirectory(dir);
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);

        // ProxiFyre отказывается ставить службу из папки, куда может писать не администратор,
        // и спотыкается об унаследованную запись «СОЗДАТЕЛЬ-ВЛАДЕЛЕЦ» (S-1-3-0).
        Run("icacls.exe", $"\"{dir}\" /inheritance:d");
        Run("icacls.exe", $"\"{dir}\" /remove:g *S-1-3-0 /T");

        File.WriteAllText(Path.Combine(dir, "LICENSES.txt"), ThirdPartyNotice);

        var config = Path.Combine(dir, "app-config.json");
        if (!File.Exists(config)) PfConfig.Load(config).Save();
    }

    static void InstallService()
    {
        int code = Run(ProxiFyreExe, "install --autostart", InstallDir);
        if (code != 0 || !ServiceInstalled)
            throw new SetupException("Не удалось зарегистрировать службу ProxiFyre.");

        // Пока не выбрано ни одного приложения, службе незачем стартовать с Windows; дальше этим управляет «Тумблер».
        ServiceCtl.SetAutoStart(false);
    }

    static void EnsureFirewallRule()
    {
        var exe = ProxiFyreExe;
        if (Diagnostics.FirewallAllows(exe) == true) return;

        int code = Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
        if (code != 0)
            throw new SetupException("Не удалось создать правило брандмауэра Windows. Его можно добавить вручную — команда есть в «Диагностике».");
    }

    static IEnumerable<string> DriverProductCodes()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) yield break;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (name.StartsWith('{') && key?.GetValue("DisplayName") is string display
                && display.StartsWith("Windows Packet Filter", StringComparison.OrdinalIgnoreCase))
                yield return name;
        }
    }

    // ---------- Вспомогательное ----------

    static string ExtractResource(string resource, string sha256, string destination)
    {
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                            ?? throw new SetupException($"В сборке нет встроенного файла {resource}."))
        using (var target = File.Create(destination))
            source.CopyTo(target);

        using var check = File.OpenRead(destination);
        var actual = Convert.ToHexStringLower(SHA256.HashData(check));
        if (actual != sha256)
            throw new SetupException($"Встроенный файл {Path.GetFileName(destination)} повреждён: контрольная сумма не совпала.");
        return destination;
    }

    static int Run(string file, string arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        using var process = Process.Start(info)!;
        // Читаем оба потока параллельно, иначе переполненный буфер подвесит дочерний процесс.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Log($"> {Path.GetFileName(file)} {arguments}\n{output}{error.Result}".TrimEnd() + $"\n  код возврата {process.ExitCode}");
        return process.ExitCode;
    }

    static void Log(string text)
    {
        lock (LogLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, text + Environment.NewLine);
        }
    }

    static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка — не страшно, Windows уберёт её сама.
        }
    }

    const string ThirdPartyNotice = """
        Эту папку создал «Тумблер». В ней лежит ProxiFyre — отдельная программа стороннего автора, без изменений.

        ProxiFyre 2.6.3
          Лицензия: GNU Affero General Public License v3.0
          Исходный код и текст лицензии: https://github.com/wiresock/proxifyre

        Windows Packet Filter 3.6.2 (драйвер, устанавливается отдельно)
          Автор: NT KERNEL. Бесплатен для личного некоммерческого использования.
          Лицензионное соглашение входит в установщик; сайт: https://www.ntkernel.com/windows-packet-filter/

        Удалить всё можно в «Тумблере»: Настройки → «Удалить компоненты».
        """;
}

/// <summary>Проверка цифровой подписи файла (Authenticode).</summary>
internal static class Trust
{
    static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>
    /// Подпись файла действительна и выдана указанной организации. Сравнивается поле O сертификата:
    /// имя CN у одного издателя бывает разным (у Microsoft — «Microsoft Corporation», «.NET» и другие).
    /// </summary>
    public static bool IsSignedBy(string file, string organization)
    {
        if (!HasValidSignature(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // у X509CertificateLoader нет чтения сертификата из подписанного файла
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            const string organizationOid = "2.5.4.10";
            var signer = certificate.SubjectName.EnumerateRelativeDistinguishedNames()
                .FirstOrDefault(part => !part.HasMultipleElements && part.GetSingleElementType().Value == organizationOid)?
                .GetSingleElementValue();
            return string.Equals(signer, organization, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    static bool HasValidSignature(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,      // WTD_UI_NONE
                dwUnionChoice = 1,   // WTD_CHOICE_FILE
                pFile = filePointer,
                dwProvFlags = 0x10,  // WTD_REVOCATION_CHECK_NONE: проверка не должна зависеть от сети
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerify, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContextFlags;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);
}
