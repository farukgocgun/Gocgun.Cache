using System.Globalization;

namespace Gocgun.Cache;

internal readonly record struct InvalidationNotice(Guid Origin, bool Prefix, string Key)
{
    public static bool TryParse(string? text, out InvalidationNotice notice)
    {
        notice = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split('|', 4, StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[0], "1", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var origin))
        {
            return false;
        }

        bool prefix;
        if (string.Equals(parts[2], "p", StringComparison.Ordinal))
        {
            prefix = true;
        }
        else if (string.Equals(parts[2], "k", StringComparison.Ordinal))
        {
            prefix = false;
        }
        else
        {
            return false;
        }

        notice = new InvalidationNotice(origin, prefix, parts[3]);
        return true;
    }

    public string Format()
    {
        var kind = Prefix ? "p" : "k";
        return string.Create(CultureInfo.InvariantCulture, $"1|{Origin:N}|{kind}|{Key}");
    }
}
