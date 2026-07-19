using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace HiveMotion.Localization;

/// <summary>
/// XAML entry point for localized text: {l:Loc Nav_Pins}. Emits a OneWay binding to the
/// LocalizationManager indexer, so the text re-resolves automatically on culture change.
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
