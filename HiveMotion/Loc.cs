using HiveMotion.Localization;

namespace HiveMotion;

/// <summary>Shortcut into LocalizationManager for code-behind (non-XAML) call sites.</summary>
public static class Loc
{
    public static string Get(string key) => LocalizationManager.Instance.Get(key);

    public static string? TryGet(string key) => LocalizationManager.Instance.TryGet(key);

    public static string Format(string key, params object?[] args) => LocalizationManager.Instance.Format(key, args);

    public static string Plural(string key, int n, params object?[] args) => LocalizationManager.Instance.Plural(key, n, args);
}
