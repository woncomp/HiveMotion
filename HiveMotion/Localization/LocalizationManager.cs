using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace HiveMotion.Localization;

/// <summary>
/// Single source of truth for UI text. Looks strings up in the embedded resx for the
/// current culture and notifies bindings (via the string indexer) whenever the culture
/// changes, so XAML text switches language without a restart.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public const string SystemSetting = "system";
    public const string ChineseCulture = "zh-CN";
    public const string EnglishCulture = "en";

    private static readonly ResourceManager Resources =
        new("HiveMotion.Localization.Strings", typeof(LocalizationManager).Assembly);

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the culture changed; code-set (non-bound) texts re-query on this.</summary>
    public event EventHandler? CultureChanged;

    public CultureInfo Culture { get; private set; } = new(ChineseCulture);

    private LocalizationManager()
    {
    }

    /// <summary>Indexer used by XAML bindings; missing keys surface as the key itself.</summary>
    public string this[string key] => Get(key);

    public string Get(string key) => Resources.GetString(key, Culture) ?? key;

    public string? TryGet(string key) => Resources.GetString(key, Culture);

    public string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>
    /// Picks "{key}_One" for n == 1 when the current culture defines it (English singular),
    /// otherwise the base key (Chinese has no plural forms, English plural).
    /// </summary>
    public string Plural(string key, int n, params object?[] args)
    {
        string format = (n == 1 ? TryGet(key + "_One") : null) ?? Get(key);
        return string.Format(Culture, format, args);
    }

    /// <summary>
    /// Applies a persisted language setting ("system" / "zh-CN" / "en"). "system" follows the
    /// OS UI language, falling back to English for anything that is not Chinese.
    /// </summary>
    public void ApplyLanguageSetting(string? setting)
    {
        CultureInfo culture = setting switch
        {
            ChineseCulture => new CultureInfo(ChineseCulture),
            EnglishCulture => new CultureInfo(EnglishCulture),
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                ? new CultureInfo(ChineseCulture)
                : new CultureInfo(EnglishCulture)
        };
        SetCulture(culture);
    }

    private void SetCulture(CultureInfo culture)
    {
        if (Equals(Culture, culture))
            return;
        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}
