namespace Gocgun.Cache;

/// <summary>
/// Settings for <see cref="IGocgunCache"/>. No tenant prefix is applied.
/// </summary>
public sealed class GocgunCacheOptions
{
    /// <summary>
    /// Gets or sets Redis bağlantı dizesi (StackExchange.Redis biçimi).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets uygulama anahtar ön eki. Kiracı kimliği taşımaz. Boş olabilir.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets L1 düşürme pub/sub kanal adı.
    /// </summary>
    public string InvalidationChannel { get; set; } = "gocgun.cache.invalidate";

    /// <summary>
    /// Gets or sets L1 bellek önbelleğindeki en fazla kayıt sayısı.
    /// </summary>
    public int L1EntryLimit { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets L2 anahtarında süre yokken L1 kaydının ömrü.
    /// </summary>
    public TimeSpan L1FallbackEntryLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets L1 kaydının ömür üst sınırı. L1 ömrü, kalan Redis süresi ile bu değerin küçüğüdür. Kaçan bir düşürme bildirimi en fazla bu süre kadar eski değer gösterir.
    /// </summary>
    public TimeSpan L1MaxEntryLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets SCAN sayfa boyu. KEYS komutu kullanılmaz.
    /// </summary>
    public int ScanPageSize { get; set; } = 250;
}
