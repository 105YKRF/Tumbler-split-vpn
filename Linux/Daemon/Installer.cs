using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tumbler.Daemon;

/// <summary>Проверка требования к системе: выполнено ли, что сломается без него и как починить.</summary>
public sealed record Requirement(string Title, string Detail, bool Ok, string? Fix = null);

/// <summary>
/// Установка и удаление: «tumbler install» и «tumbler uninstall» от root (окно запускает их через pkexec).
/// Ставит себя в /opt/tumbler, туда же hev-socks5-tunnel, и регистрирует службу systemd.
/// Прогресс печатается строками «STEP …», ошибки — «ERROR …»: их читает мастер установки в окне.
/// </summary>
internal static class Installer
{
    public const string Dir = "/opt/tumbler";
    public const string ExePath = Dir + "/tumbler";
    public const string HevPath = Dir + "/hev-socks5-tunnel";
    public const string IconPath = Dir + "/tumbler.png";
    public const string HevVersion = "2.18.0";

    const string ConfigDir = "/etc/tumbler";
    const string UsersFile = ConfigDir + "/daemon.json";
    const string UnitPath = "/etc/systemd/system/tumbler.service";
    const string DesktopPath = "/usr/share/applications/tumbler.desktop";
    const string HevSha256 = "1903b23869cbd12b7b57045ca161c0f90314d9c4ef2835e7a9e4a3179bf7d925";

    const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static bool IsInstalled => File.Exists(UnitPath) && File.Exists(ExePath);

    /// <summary>Что нужно системе. Проверка работает и без прав root — её показывает мастер установки.</summary>
    public static List<Requirement> Check()
    {
        string install = PackageHint();
        return
        [
            new("64-битная система на Intel или AMD", "Сборка «Тумблера» — для x86_64.",
                RuntimeInformation.OSArchitecture == Architecture.X64),
            new("Ядро Linux 5.13 или новее", $"Нужно nftables, чтобы узнавать сокеты по cgroup. Сейчас: {Environment.OSVersion.Version.ToString(3)}.",
                !OperatingSystem.IsLinux() || Environment.OSVersion.Version >= new Version(5, 13), "Обнови систему: Ubuntu 22.04, Debian 12, Fedora 36 и новее подходят."),
            new("cgroup v2", "По ней «Тумблер» отличает трафик выбранных приложений. Есть во всех дистрибутивах последних лет.",
                Cgroups.V2, "Обнови дистрибутив или включи единую иерархию cgroup (параметр ядра systemd.unified_cgroup_hierarchy=1)."),
            new("systemd", "Запускает службу «Тумблера» вместе с системой.",
                Directory.Exists("/run/systemd/system")),
            new("nftables", "Помечает пакеты выбранных приложений.",
                Sys.Find("nft") is not null, $"Установи пакет nftables: {install} nftables"),
            new("iproute2", "Отправляет помеченные пакеты в туннель.",
                Sys.Find("ip") is not null, $"Установи пакет iproute2: {install} iproute2"),
        ];
    }

    static string PackageHint() =>
        Sys.Find("apt") is not null ? "sudo apt install"
        : Sys.Find("dnf") is not null ? "sudo dnf install"
        : Sys.Find("pacman") is not null ? "sudo pacman -S"
        : Sys.Find("zypper") is not null ? "sudo zypper install"
        : "установи через пакетный менеджер:";

    public static HashSet<uint> LoadUsers()
    {
        try
        {
            if (File.Exists(UsersFile))
                return JsonSerializer.Deserialize<UsersConfig>(File.ReadAllText(UsersFile), Wire.Json)?.Users.ToHashSet() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Sys.Log($"Не прочитался {UsersFile}: {ex.Message}");
        }
        return [];
    }

    sealed class UsersConfig
    {
        public List<uint> Users { get; set; } = new();
    }

    // ---------- Установка ----------

    public static int Install(uint user)
    {
        if (!Sys.IsRoot) return Fail("Установку нужно запускать от root: sudo tumbler install --user $(id -u)");
        try
        {
            Step("Проверяю систему");
            var missing = Check().Where(r => !r.Ok).ToList();
            if (missing.Count > 0)
                return Fail(string.Join("; ", missing.Select(r => r.Fix is null ? $"нет: {r.Title}" : $"нет: {r.Title} — {r.Fix}")));

            if (Sys.Run("systemctl", "is-active --quiet tumbler.service").Code == 0)
            {
                Step("Останавливаю прежнюю версию службы");
                Sys.Run("systemctl", "stop tumbler.service");
            }

            Step($"Копирую «Тумблер» в {Dir}");
            Directory.CreateDirectory(Dir);
            if (!string.Equals(Environment.ProcessPath, ExePath, StringComparison.Ordinal))
            {
                CopyReplacing(Environment.ProcessPath!, ExePath);
                // Библиотеки отрисовки окна (Skia, HarfBuzz) лежат рядом с исполняемым файлом.
                foreach (var library in Directory.GetFiles(AppContext.BaseDirectory, "*.so"))
                    CopyReplacing(library, System.IO.Path.Combine(Dir, System.IO.Path.GetFileName(library)));
            }
            File.SetUnixFileMode(ExePath, Executable);

            Step($"Распаковываю hev-socks5-tunnel {HevVersion}");
            ExtractResource("Payload.hev", HevSha256, HevPath);
            File.SetUnixFileMode(HevPath, Executable);
            ExtractResource("Assets.tumbler.png", null, IconPath);
            File.WriteAllText(Dir + "/LICENSES.txt", ThirdPartyNotice + ReadResource("Payload.hev.LICENSE"));

            Step("Записываю настройки службы");
            Directory.CreateDirectory(ConfigDir);
            var users = LoadUsers();
            users.Add(user);
            File.WriteAllText(UsersFile, JsonSerializer.Serialize(new UsersConfig { Users = users.Order().ToList() }, Wire.Json));

            File.WriteAllText(UnitPath, Unit);
            File.SetUnixFileMode(UnitPath, Readable);
            File.WriteAllText(DesktopPath, DesktopEntry);
            File.SetUnixFileMode(DesktopPath, Readable);

            Step("Запускаю службу");
            Sys.Run("systemctl", "daemon-reload");
            var (code, output) = Sys.Run("systemctl", "enable --now tumbler.service");
            if (code != 0) return Fail($"systemd не запустил службу: {output}");

            for (int i = 0; i < 60 && !File.Exists(Wire.SocketPath); i++) Thread.Sleep(250);
            if (!File.Exists(Wire.SocketPath))
                return Fail("Служба запустилась, но не отвечает. Посмотри журнал: journalctl -u tumbler -n 50");

            Console.WriteLine("DONE");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(ex.Message);
        }
    }

    public static int Uninstall()
    {
        if (!Sys.IsRoot) return Fail("Удаление нужно запускать от root: sudo tumbler uninstall");
        Step("Останавливаю службу");
        Sys.Run("systemctl", "disable --now tumbler.service");

        // Если служба была мертва, за ней мог остаться мусор в сети и cgroup.
        Net.CleanupLeftovers();
        if (Cgroups.V2) Cgroups.RemoveAll();

        Step("Удаляю файлы");
        foreach (var file in new[] { UnitPath, DesktopPath })
            if (File.Exists(file)) File.Delete(file);
        foreach (var dir in new[] { Dir, ConfigDir, "/run/tumbler" })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Sys.Run("systemctl", "daemon-reload");

        Console.WriteLine("DONE");
        return 0;
    }

    // ---------- Вспомогательное ----------

    static void Step(string text) => Console.WriteLine($"STEP {text}");

    static int Fail(string text)
    {
        Console.WriteLine($"ERROR {text}");
        return 2;
    }

    /// <summary>Замена через переименование: работает, даже если старый файл сейчас запущен.</summary>
    static void CopyReplacing(string source, string destination)
    {
        var temp = destination + ".new";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    static void ExtractResource(string name, string? sha256, string destination)
    {
        var temp = destination + ".new";
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                            ?? throw new InvalidDataException($"в сборке нет встроенного файла {name}"))
        using (var target = File.Create(temp))
            source.CopyTo(target);

        if (sha256 is not null)
        {
            using var check = File.OpenRead(temp);
            if (Convert.ToHexStringLower(SHA256.HashData(check)) != sha256)
                throw new InvalidDataException($"встроенный файл {System.IO.Path.GetFileName(destination)} повреждён: контрольная сумма не совпала");
        }
        File.Move(temp, destination, overwrite: true);
    }

    static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream is null ? "" : new StreamReader(stream).ReadToEnd();
    }

    const string Unit = $"""
        [Unit]
        Description=Tumbler: selected applications through a SOCKS5 proxy
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        ExecStart={ExePath} daemon
        Restart=on-failure
        RestartSec=2
        Environment=HOME=/root

        [Install]
        WantedBy=multi-user.target
        """;

    const string DesktopEntry = $"""
        [Desktop Entry]
        Type=Application
        Name=Тумблер
        Comment=Выбранные приложения через VPN, остальные напрямую
        Exec={ExePath}
        Icon={IconPath}
        Terminal=false
        Categories=Network;
        """;

    const string ThirdPartyNotice = """
        Эту папку создал «Тумблер». В ней лежит hev-socks5-tunnel — отдельная программа стороннего автора, без изменений.

        hev-socks5-tunnel 2.18.0
          Исходный код: https://github.com/heiher/hev-socks5-tunnel
          Лицензия (MIT):

        """;
}
