using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tumbler;

internal enum ServiceState { NotInstalled, Stopped, Starting, Stopping, Running }

/// <summary>Управление службой ProxiFyre: она и перехватывает трафик, «Тумблер» только настраивает её.</summary>
internal static class ServiceCtl
{
    public const string Name = "ProxiFyreService";

    const uint SC_MANAGER_CONNECT = 0x0001;
    const uint SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_STATUS = 0x0004, SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;
    const uint SERVICE_NO_CHANGE = 0xFFFFFFFF, SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3;
    const uint SERVICE_CONTROL_STOP = 1;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_NOT_ACTIVE = 1062;

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static ServiceState Query(string name = Name)
    {
        try
        {
            return With(SERVICE_QUERY_STATUS, ReadState, name);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_SERVICE_DOES_NOT_EXIST)
        {
            return ServiceState.NotInstalled;
        }
    }

    /// <summary>Папка, из которой установлена служба (там же лежит app-config.json).</summary>
    public static string? InstallDir()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Name}");
        if (key?.GetValue("ImagePath") is not string image) return null;

        image = image.Trim();
        string exe;
        if (image.StartsWith('"'))
        {
            int end = image.IndexOf('"', 1);
            exe = end > 0 ? image[1..end] : image.Trim('"');
        }
        else
        {
            int end = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = end > 0 ? image[..(end + 4)] : image;
        }
        return Path.GetDirectoryName(exe);
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static void Stop() => With(SERVICE_STOP | SERVICE_QUERY_STATUS, svc =>
    {
        if (ReadState(svc) == ServiceState.Stopped) return 0;
        if (!Native.ControlService(svc, SERVICE_CONTROL_STOP, out _))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_SERVICE_NOT_ACTIVE) throw new Win32Exception(error);
        }
        WaitFor(svc, ServiceState.Stopped);
        return 0;
    });

    public static void Start() => With(SERVICE_START | SERVICE_QUERY_STATUS, svc =>
    {
        if (!Native.StartService(svc, 0, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_SERVICE_ALREADY_RUNNING) throw new Win32Exception(error);
        }
        WaitFor(svc, ServiceState.Running);
        return 0;
    });

    /// <summary>Автозапуск вместе с Windows. На паузе служба не должна сама подниматься после перезагрузки.</summary>
    public static void SetAutoStart(bool auto) => With(SERVICE_CHANGE_CONFIG, svc =>
    {
        uint start = auto ? SERVICE_AUTO_START : SERVICE_DEMAND_START;
        if (!Native.ChangeServiceConfig(svc, SERVICE_NO_CHANGE, start, SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception();
        return 0;
    });

    static ServiceState ReadState(IntPtr svc)
    {
        if (!Native.QueryServiceStatus(svc, out var status)) throw new Win32Exception();
        return status.CurrentState switch
        {
            1 => ServiceState.Stopped,
            2 => ServiceState.Starting,
            3 => ServiceState.Stopping,
            _ => ServiceState.Running,
        };
    }

    static void WaitFor(IntPtr svc, ServiceState wanted)
    {
        var deadline = DateTime.UtcNow + Timeout;
        bool sawPending = false;
        while (true)
        {
            var state = ReadState(svc);
            if (state == wanted) return;
            sawPending |= state is ServiceState.Starting or ServiceState.Stopping;
            // Служба упала на старте: была «запускается» и вернулась в «остановлена».
            if (wanted == ServiceState.Running && state == ServiceState.Stopped && sawPending)
                throw new InvalidOperationException("служба ProxiFyre не запустилась — посмотри её лог");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("служба ProxiFyre не ответила за 20 секунд");
            Thread.Sleep(150);
        }
    }

    static T With<T>(uint access, Func<IntPtr, T> body, string name = Name)
    {
        var scm = Native.OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = Native.OpenService(scm, name, access);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                return body(svc);
            }
            finally
            {
                Native.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }
}
