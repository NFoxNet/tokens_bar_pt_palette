using System;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;
using PaletteSettings = Microsoft.CommandPalette.Extensions.Toolkit.Settings;

namespace TokensLimitsExtension.Settings;

public sealed partial class TokensLimitsSettings : JsonSettingsManager, IUsageRefreshSettings, IDisposable
{
    private const int DefaultRefreshIntervalSeconds = 60;
    private const string SettingsNamespace = "tokensLimits";

    private readonly ChoiceSetSetting _refreshInterval = new(
        $"{SettingsNamespace}.refreshInterval",
        "Частота обновления",
        "Как часто получать актуальные данные о лимитах.",
        [
            new ChoiceSetSetting.Choice("1 минута", "60"),
            new ChoiceSetSetting.Choice("30 секунд", "30"),
            new ChoiceSetSetting.Choice("5 минут", "300"),
            new ChoiceSetSetting.Choice("15 минут", "900"),
        ]);

    private bool _disposed;

    public TokensLimitsSettings()
    {
        FilePath = SettingsJsonPath();
        Settings.Add(_refreshInterval);
        LoadSettings();
        Settings.SettingsChanged += OnSettingsChanged;
    }

    public TimeSpan RefreshInterval
    {
        get
        {
            if (int.TryParse(_refreshInterval.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(DefaultRefreshIntervalSeconds);
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Settings.SettingsChanged -= OnSettingsChanged;
        Changed = null;
        GC.SuppressFinalize(this);
    }

    private static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("TokensLimitsExtension");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{SettingsNamespace}.settings.json");
    }

    private void OnSettingsChanged(object? sender, PaletteSettings args)
    {
        SaveSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
