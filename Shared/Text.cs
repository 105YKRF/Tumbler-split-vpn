namespace Tumbler;

/// <summary>Подсказка порта: номер, подпись на кнопке и всплывающее пояснение.</summary>
public sealed record PortPreset(int Port, string Label, string Hint);

internal static class Text
{
    /// <summary>«1 процесс», «3 процесса», «12 процессов».</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int tens = n % 100, units = n % 10;
        var word = units == 1 && tens != 11 ? one
            : units is >= 2 and <= 4 && tens is < 12 or > 14 ? few
            : many;
        return $"{n} {word}";
    }
}
