using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Tumbler.Daemon;

/// <summary>
/// UNIX-сокет управления. Окно «Тумблера» работает с правами пользователя и говорит со службой через него.
/// Кто подключился, узнаём у ядра (SO_PEERCRED): пускаем root и пользователей, указанных при установке.
/// </summary>
internal sealed class DaemonServer(Engine engine, IReadOnlySet<uint> allowedUsers)
{
    const int SolSocket = 1, SoPeerCred = 17;

    public async Task RunAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Wire.SocketPath)!);
        if (File.Exists(Wire.SocketPath)) File.Delete(Wire.SocketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Wire.SocketPath));
        // Подключиться может кто угодно, но команды выполняются только для разрешённых пользователей.
        File.SetUnixFileMode(Wire.SocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(16);

        while (!cancel.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = Task.Run(() => Serve(client), CancellationToken.None);
        }

        try
        {
            File.Delete(Wire.SocketPath);
        }
        catch (IOException)
        {
        }
    }

    void Serve(Socket client)
    {
        using (client)
        {
            try
            {
                using var stream = new NetworkStream(client, ownsSocket: false) { ReadTimeout = 5000, WriteTimeout = 5000 };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                Response response;
                uint uid = PeerUid(client);
                if (uid != 0 && !allowedUsers.Contains(uid))
                {
                    response = new Response { Error = $"пользователю с UID {uid} управлять «Тумблером» не разрешено — переустанови его из-под своей учётной записи" };
                }
                else
                {
                    var line = reader.ReadLine();
                    var request = line is null ? null : JsonSerializer.Deserialize<Request>(line, Wire.Json);
                    response = request is null ? new Response { Error = "пустой запрос" } : engine.Handle(request);
                }
                writer.WriteLine(JsonSerializer.Serialize(response, Wire.Json));
            }
            catch (Exception ex) when (ex is IOException or JsonException or SocketException)
            {
                Sys.Log($"Ошибка обмена с окном: {ex.Message}");
            }
        }
    }

    static uint PeerUid(Socket socket)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<byte> credentials = stackalloc byte[12];
        socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials);
        return MemoryMarshal.Read<uint>(credentials[4..]);
    }
}

/// <summary>Точка входа службы: tumbler daemon (запускает systemd от root).</summary>
internal static class DaemonMain
{
    public static int Run()
    {
        if (!Sys.IsRoot)
        {
            Console.Error.WriteLine("Служба «Тумблера» должна работать от root — её запускает systemd.");
            return 1;
        }

        using var cancel = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancel.Cancel();
        });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            cancel.Cancel();
        });

        using var engine = new Engine(Installer.HevPath);
        var server = new DaemonServer(engine, Installer.LoadUsers());
        Task.WaitAll(engine.RunAsync(cancel.Token), server.RunAsync(cancel.Token));
        return 0;
    }
}
