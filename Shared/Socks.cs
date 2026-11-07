using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Tumbler;

/// <summary>Минимальный SOCKS5-клиент для проверок: жив ли порт, умеет ли UDP, ходит ли через него интернет.</summary>
internal static class Socks
{
    const string TestHost = "www.gstatic.com";

    /// <summary>Отвечает ли на порту SOCKS5 (быстрая проверка, вызывается каждые пару секунд).</summary>
    public static bool Probe(string host, int port)
    {
        try
        {
            using var client = Connect(host, port, 700);
            return client is not null;
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return false;
        }
    }

    /// <summary>Поддерживает ли прокси UDP (команда UDP ASSOCIATE) — без неё не работает голос.</summary>
    public static bool UdpAssociate(string host, int port)
    {
        try
        {
            using var client = Connect(host, port, 2000);
            if (client is null) return false;
            var stream = client.GetStream();
            stream.Write([5, 3, 0, 1, 0, 0, 0, 0, 0, 0]);
            var reply = new byte[10];
            return ReadExact(stream, reply, 4) && reply[1] == 0;
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return false;
        }
    }

    /// <summary>Запрашивает через прокси тестовую страницу Google. Возвращает время ответа или текст ошибки.</summary>
    public static (bool Ok, string Detail) HttpThrough(string host, int port)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = Connect(host, port, 6000);
            if (client is null) return (false, "прокси не отвечает");
            var stream = client.GetStream();

            // CONNECT по доменному имени: адрес разрешает сам прокси, как это делают приложения.
            var name = Encoding.ASCII.GetBytes(TestHost);
            stream.Write([5, 1, 0, 3, (byte)name.Length, .. name, 0, 80]);
            var reply = new byte[4];
            if (!ReadExact(stream, reply, 4)) return (false, "прокси оборвал соединение");
            if (reply[1] != 0) return (false, $"прокси отказал в соединении (код {reply[1]})");

            int addressLength = reply[3] switch { 1 => 4, 4 => 16, _ => -1 };
            if (addressLength < 0)
            {
                var length = new byte[1];
                if (!ReadExact(stream, length, 1)) return (false, "прокси оборвал соединение");
                addressLength = length[0];
            }
            if (!ReadExact(stream, new byte[addressLength + 2], addressLength + 2)) return (false, "прокси оборвал соединение");

            stream.Write(Encoding.ASCII.GetBytes($"GET /generate_204 HTTP/1.1\r\nHost: {TestHost}\r\nConnection: close\r\n\r\n"));
            var head = new byte[12];
            if (!ReadExact(stream, head, head.Length)) return (false, "сервер не ответил через прокси");

            var status = Encoding.ASCII.GetString(head);
            return status.EndsWith("204") ? (true, $"{timer.ElapsedMilliseconds} мс") : (false, $"неожиданный ответ «{status}»");
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return (false, "нет ответа за 6 секунд");
        }
    }

    /// <summary>Соединяется и проходит приветствие SOCKS5 без авторизации. null — на порту не SOCKS5 или никого нет.</summary>
    static TcpClient? Connect(string host, int port, int timeoutMs)
    {
        var client = new TcpClient();
        try
        {
            if (!client.ConnectAsync(host, port).Wait(timeoutMs)) throw new TimeoutException();
            var stream = client.GetStream();
            stream.ReadTimeout = stream.WriteTimeout = timeoutMs;
            stream.Write([5, 1, 0]);
            var reply = new byte[2];
            if (ReadExact(stream, reply, 2) && reply[0] == 5 && reply[1] == 0) return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
        client.Dispose();
        return null;
    }

    static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    static bool IsNetworkError(Exception ex) =>
        ex is SocketException or IOException or AggregateException or TimeoutException;
}
