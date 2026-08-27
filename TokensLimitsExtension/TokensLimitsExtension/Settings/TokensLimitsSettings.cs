using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using PaletteSettings = Microsoft.CommandPalette.Extensions.Toolkit.Settings;

namespace TokensLimitsExtension.Settings;

public sealed partial class TokensLimitsSettings : JsonSettingsManager, IUsageRefreshSettings, IUsageProviderConfiguration, IDisposable
{
    private const int MinimumRefreshIntervalSeconds = 30;
    private const int MaximumRefreshIntervalSeconds = 3600;
    private const int DefaultRefreshIntervalSeconds = 60;
    private const string SettingsNamespace = "tokensLimits";
    private const string SecretMask = "••••••••";

    private readonly TextSetting _refreshInterval = new(
        $"{SettingsNamespace}.refreshInterval",
        "Частота обновления",
        $"Интервал в секундах, от {MinimumRefreshIntervalSeconds} до {MaximumRefreshIntervalSeconds}.",
        DefaultRefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture))
    {
        IsRequired = true,
        Placeholder = $"{MinimumRefreshIntervalSeconds}–{MaximumRefreshIntervalSeconds}",
    };

    private readonly Dictionary<string, ToggleSetting> _providerToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextSetting> _providerFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _secretFieldKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProtectedSecretStore _secretStore;

    private bool _disposed;
    private bool _handlingSettingsChange;

    public TokensLimitsSettings()
    {
        FilePath = SettingsJsonPath();
        _secretStore = new ProtectedSecretStore(SecretsJsonPath());
        Settings.Add(_refreshInterval);
        AddProviderSettings();
        LoadSettings();
        ValidateRefreshInterval();
        MigrateAndMaskLoadedSecrets();
        Settings.SettingsChanged += OnSettingsChanged;
    }

    public TimeSpan RefreshInterval
    {
        get
        {
            if (TryGetRefreshIntervalSeconds(out var seconds))
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
        if (_providerFields.TryGetValue(FieldKey(providerId, key), out var setting))
        {
            if (_secretFieldKeys.Contains(setting.Key))
            {
                var protectedValue = Normalize(_secretStore.Get(setting.Key));
                if (protectedValue is not null)
                {
                    return protectedValue;
                }

                var unprotectedValue = Normalize(setting.Value);
                if (unprotectedValue is not null && !IsSecretMask(unprotectedValue))
                {
                    return unprotectedValue;
                }
            }
            else
            {
                var configured = Normalize(setting.Value);
                if (configured is not null)
                {
                    return configured;
                }
            }
        }

        var descriptor = UsageProviderDescriptorRegistry.All
            .FirstOrDefault(candidate => candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        var field = descriptor?.Settings.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return field?.EnvironmentVariable is null
            ? null
            : Normalize(Environment.GetEnvironmentVariable(field.EnvironmentVariable));
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
        _secretStore.Dispose();
        Changed = null;
        GC.SuppressFinalize(this);
    }

    private static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("TokensLimitsExtension");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{SettingsNamespace}.settings.json");
    }

    private static string SecretsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("TokensLimitsExtension");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{SettingsNamespace}.secrets.json");
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
                    Placeholder = field.IsSecret
                        ? "Сохранено; введите новое значение для замены"
                        : string.Empty,
                };
                _providerFields.Add(setting.Key, setting);
                if (field.IsSecret)
                {
                    _secretFieldKeys.Add(setting.Key);
                }
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
        if (_disposed || _handlingSettingsChange)
        {
            return;
        }

        _handlingSettingsChange = true;
        try
        {
            ValidateRefreshInterval();
            if (PersistAndMaskSecrets())
            {
                SaveSettings();
            }
        }
        finally
        {
            _handlingSettingsChange = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ValidateRefreshInterval()
    {
        if (TryGetRefreshIntervalSeconds(out _))
        {
            _refreshInterval.ErrorMessage = string.Empty;
            return true;
        }

        _refreshInterval.ErrorMessage =
            $"Введите целое число от {MinimumRefreshIntervalSeconds} до {MaximumRefreshIntervalSeconds} секунд.";
        return false;
    }

    private bool TryGetRefreshIntervalSeconds(out int seconds)
    {
        return int.TryParse(_refreshInterval.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
            && seconds is >= MinimumRefreshIntervalSeconds and <= MaximumRefreshIntervalSeconds;
    }

    private void MigrateAndMaskLoadedSecrets()
    {
        if (!PersistAndMaskSecrets())
        {
            return;
        }

        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TokensLimits] ERROR: unable to save masked settings: {ex.Message}");
        }
    }

    private bool PersistAndMaskSecrets()
    {
        foreach (var setting in _providerFields.Values)
        {
            if (!_secretFieldKeys.Contains(setting.Key))
            {
                continue;
            }

            var value = Normalize(setting.Value);
            if (IsSecretMask(value))
            {
                continue;
            }

            try
            {
                if (value is null)
                {
                    _secretStore.Remove(setting.Key);
                }
                else
                {
                    _secretStore.Set(setting.Key, value);
                    setting.Value = SecretMask;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TokensLimits] ERROR: unable to protect setting '{setting.Key}': {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private static bool IsSecretMask(string? value)
        => string.Equals(value, SecretMask, StringComparison.Ordinal);
}
