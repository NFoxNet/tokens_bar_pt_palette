using System;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using PaletteSettings = Microsoft.CommandPalette.Extensions.Toolkit.Settings;

namespace TokensLimitsExtension.Settings;

public sealed partial class TokensLimitsSettings : JsonSettingsManager, IUsageRefreshSettings, IUsageProviderConfiguration, IDisposable
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

    private readonly Dictionary<string, ToggleSetting> _providerToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextSetting> _providerFields = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public TokensLimitsSettings()
    {
        FilePath = SettingsJsonPath();
        Settings.Add(_refreshInterval);
        AddProviderSettings();
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

    public bool IsEnabled(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providerToggles.TryGetValue(providerId, out var setting) && setting.Value;
    }

    public string? GetValue(string providerId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _providerFields.TryGetValue(FieldKey(providerId, key), out var setting)
            ? Normalize(setting.Value)
            : null;
    }

    public IReadOnlyList<UsageProviderDescriptor> EnabledDescriptors
        => UsageProviderDescriptorRegistry.All
            .Where(descriptor => IsEnabled(descriptor.Id))
            .ToArray();

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

    private void AddProviderSettings()
    {
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var toggle = new ToggleSetting(
                ProviderKey(descriptor.Id),
                $"Включить {descriptor.DisplayName}",
                descriptor.SourceDescription ?? $"Показывать данные {descriptor.DisplayName}.",
                descriptor.DefaultEnabled);
            _providerToggles.Add(descriptor.Id, toggle);
            Settings.Add(toggle);

            foreach (var field in descriptor.Settings)
            {
                var setting = new TextSetting(
                    FieldKey(descriptor.Id, field.Key),
                    field.Label,
                    BuildFieldDescription(field),
                    field.DefaultValue ?? string.Empty)
                {
                    IsRequired = false,
                    Placeholder = field.IsSecret ? "Введите значение или задайте переменную окружения" : string.Empty,
                };
                _providerFields.Add(setting.Key, setting);
                Settings.Add(setting);
            }
        }
    }

    private static string ProviderKey(string providerId)
        => $"{SettingsNamespace}.providers.{providerId}.enabled";

    private static string FieldKey(string providerId, string fieldKey)
        => $"{SettingsNamespace}.providers.{providerId}.{fieldKey}";

    private static string BuildFieldDescription(UsageProviderSettingDescriptor field)
        => string.IsNullOrWhiteSpace(field.EnvironmentVariable)
            ? field.Description
            : $"{field.Description} Переменная окружения: {field.EnvironmentVariable}.";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnSettingsChanged(object? sender, PaletteSettings args)
    {
        SaveSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
