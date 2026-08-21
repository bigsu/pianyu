using System.Text;

namespace Pianyu.Core;

public static class SearchText
{
    private static readonly string[] PinyinInitials =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "W", "X", "Y", "Z"
    ];

    private static readonly int[] PinyinAreas =
    [
        -20319, -20284, -19776, -19219, -18711, -18527, -18240, -17923, -17418, -16475, -16213, -15641,
        -15166, -14923, -14915, -14631, -14150, -14091, -13319, -12839, -12557, -11848, -11056
    ];

    static SearchText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string GetPinyinInitials(string text)
    {
        var builder = new StringBuilder(text.Length);
        var encoding = Encoding.GetEncoding("GB2312");
        foreach (var character in text)
        {
            if (character <= 127)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                continue;
            }

            var bytes = encoding.GetBytes(character.ToString());
            if (bytes.Length < 2)
            {
                continue;
            }

            var code = bytes[0] * 256 + bytes[1] - 65536;
            for (var i = PinyinAreas.Length - 1; i >= 0; i--)
            {
                if (code >= PinyinAreas[i])
                {
                    builder.Append(PinyinInitials[i].ToLowerInvariant());
                    break;
                }
            }
        }
        return builder.ToString();
    }

    public static int LevenshteinDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[right.Length];
    }

    public static bool IsFuzzyMatch(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (GetPinyinInitials(candidate).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var tolerance = query.Length >= 5 ? Math.Max(2, query.Length / 4) : 1;
        return query.Length >= 3 && LevenshteinDistance(query, candidate) <= tolerance;
    }
}
