namespace Gocgun.Cache;

internal static class CacheKeyRules
{
    public static string FullKey(string keyPrefix, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        RejectGlob(keyPrefix, nameof(keyPrefix));
        RejectGlob(key, nameof(key));
        return string.Concat(keyPrefix, key);
    }

    public static string FullPrefix(string keyPrefix, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        RejectGlob(keyPrefix, nameof(keyPrefix));
        RejectGlob(prefix, nameof(prefix));
        return string.Concat(keyPrefix, prefix);
    }

    public static void RejectGlob(string value, string paramName)
    {
        if (value.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            throw new ArgumentException("SCAN glob characters are not allowed.", paramName);
        }
    }
}
