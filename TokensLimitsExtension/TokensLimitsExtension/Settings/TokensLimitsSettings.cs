using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Localization;
using System.IO;
using System.Linq;
using TokensLimitsExtension.Core.Providers;
using PaletteSettings = Microsoft.CommandPalette.Extensions.Toolkit.Settings;

namespace TokensLimitsExtension.Settings;

public sealed partial class TokensLimitsSettings : JsonSettingsManager, IUsageRefreshSettings, IUsageProviderConfiguration, IUsageProviderConfigurationChangeSource, IDisposable
{
    private const int MinimumRefreshIntervalSeconds = 30;
    private const int MaximumRefreshIntervalSeconds = 3600;
    private const int DefaultRefreshIntervalSeconds = 60;
    private const string StorageDirectoryName = "TokensLimitsExtension";
    private const string SettingsNamespace = "tokensLimits";
    private const string SecretMask = "••••••••";

    private readonly ChoiceSetSetting _language;

    private readonly TextSetting _refreshInterval;

    private readonly Dictionary<string, ToggleSetting> _providerToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextSetting> _providerFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _secretFieldKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProtectedSecretStore _secretStore;
    private readonly JsonLocalizationService _localization;

    private bool _disposed;
    private bool _handlingSettingsChange;
    private string _providerConfigurationFingerprint = string.Empty;

    public TokensLimitsSettings()
        : this(ResolveDefaultStorage())
    {
    }

    internal TokensLimitsSettings(string storageDirectory, string? hostDirectory = null)
        : this(ResolveStorage(storageDirectory, hostDirectory))
    {
    }

    private TokensLimitsSettings(SettingsStorage storage)
    {
        FilePath = storage.SettingsPath;
        _secretStore = new ProtectedSecretStore(storage.SecretsPath);
        _localization = new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "lang"),
            Path.Combine(storage.Directory, "lang"));
        _language = new ChoiceSetSetting(
            $"{SettingsNamespace}.language",
            [
                new ChoiceSetSetting.Choice(_localization.GetString("settings.auto"), "auto"),
                .. _localization.Languages.Select(language => new ChoiceSetSetting.Choice(language.NativeName, language.Culture)),
            ]);
        _refreshInterval = new TextSetting(
            $"{SettingsNamespace}.refreshInterval",
            string.Empty,
            string.Empty,
            DefaultRefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture))
        {
            IsRequired = true,
            Placeholder = $"{MinimumRefreshIntervalSeconds}–{MaximumRefreshIntervalSeconds}",
        };
        Settings.Add(_language);
        Settings.Add(_refreshInterval);
        AddProviderSettings();
        LoadSettings();
        _localization.ApplyPreference(_language.Value, notify: false);
        RefreshLocalizedSettings();
        ValidateRefreshInterval();
        MigrateAndMaskLoadedSecrets();
        _providerConfigurationFingerprint = GetProviderConfigurationFingerprint();
        Settings.SettingsChanged += OnSettingsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
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

    public event EventHandler? ProviderConfigurationChanged;

    public ILocalizationService Localization => _localization;

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
        _localization.LanguageChanged -= OnLanguageChanged;
        _secretStore.Dispose();
        Changed = null;
        ProviderConfigurationChanged = null;
        GC.SuppressFinalize(this);
    }

    private static SettingsStorage ResolveDefaultStorage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application data path is unavailable.");
        }

        var canonicalDirectory = Path.Combine(localAppData, StorageDirectoryName);

        string? hostDirectory = null;
        try
        {
            hostDirectory = Utilities.BaseSettingsPath(StorageDirectoryName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TokensLimits] WARNING: unable to resolve host settings path: {ex.Message}");
        }

        return ResolveStorage(canonicalDirectory, hostDirectory);
    }

    internal static SettingsStorage ResolveStorage(string canonicalDirectory, string? hostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectory);
        Directory.CreateDirectory(canonicalDirectory);

        if (!string.IsNullOrWhiteSpace(hostDirectory))
        {
            MigrateHostStorage(hostDirectory, canonicalDirectory);
        }

        return new SettingsStorage(
            canonicalDirectory,
            Path.Combine(canonicalDirectory, $"{SettingsNamespace}.settings.json"),
            Path.Combine(canonicalDirectory, $"{SettingsNamespace}.secrets.json"));
    }

    private static void MigrateHostStorage(string hostDirectory, string canonicalDirectory)
    {
        var normalizedHostDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostDirectory));
        var normalizedCanonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalDirectory));
        if (string.Equals(normalizedHostDirectory, normalizedCanonicalDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hostSettingsPath = Path.Combine(hostDirectory, $"{SettingsNamespace}.settings.json");
        var hostSecretsPath = Path.Combine(hostDirectory, $"{SettingsNamespace}.secrets.json");
        var canonicalSettingsPath = Path.Combine(canonicalDirectory, $"{SettingsNamespace}.settings.json");
        var canonicalSecretsPath = Path.Combine(canonicalDirectory, $"{SettingsNamespace}.secrets.json");

        try
        {
            // A package-local secret store may contain the latest key after an update.
            // Keep its settings file paired with the encrypted secrets during migration.
            if (File.Exists(hostSecretsPath) && !File.Exists(canonicalSecretsPath))
            {
                if (File.Exists(hostSettingsPath))
                {
                    File.Copy(hostSettingsPath, canonicalSettingsPath, overwrite: true);
                }

                File.Copy(hostSecretsPath, canonicalSecretsPath, overwrite: false);
                return;
            }

            if (!File.Exists(canonicalSettingsPath) && File.Exists(hostSettingsPath))
            {
                File.Copy(hostSettingsPath, canonicalSettingsPath, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            // The extension can still start with the canonical store if another host has
            // already completed the migration. Do not log setting or secret values.
            Debug.WriteLine($"[TokensLimits] WARNING: unable to migrate host storage: {ex.Message}");
        }
    }

    private void AddProviderSettings()
    {
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var toggle = new ToggleSetting(
                ProviderKey(descriptor.Id),
                string.Empty,
                string.Empty,
                descriptor.DefaultEnabled);
            _providerToggles.Add(descriptor.Id, toggle);
            Settings.Add(toggle);

            foreach (var field in descriptor.Settings)
            {
                var setting = new TextSetting(
                    FieldKey(descriptor.Id, field.Key),
                    string.Empty,
                    string.Empty,
                    field.DefaultValue ?? string.Empty)
                {
                    IsRequired = false,
                    Placeholder = field.IsSecret
                        ? string.Empty
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

    private void RefreshLocalizedSettings()
    {
        _language.Label = _localization.GetString("settings.language");
        _language.Description = _localization.GetString("settings.languageDescription");
        _language.Choices =
        [
            new ChoiceSetSetting.Choice(_localization.GetString("settings.auto"), "auto"),
            .. _localization.Languages.Select(language => new ChoiceSetSetting.Choice(language.NativeName, language.Culture)),
        ];

        _refreshInterval.Label = _localization.GetString("settings.refreshInterval");
        _refreshInterval.Description = _localization.Format(
            "settings.refreshIntervalDescription",
            MinimumRefreshIntervalSeconds,
            MaximumRefreshIntervalSeconds);

        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var toggle = _providerToggles[descriptor.Id];
            toggle.Label = _localization.Format("settings.enableProvider", descriptor.DisplayName);
            toggle.Description = _localization.Format("settings.providerSource", descriptor.DisplayName);

            foreach (var field in descriptor.Settings)
            {
                var setting = _providerFields[FieldKey(descriptor.Id, field.Key)];
                setting.Label = _localization.GetString($"settings.field.{field.Key}.label", field.Label);
                setting.Description = BuildFieldDescription(field);
                setting.Placeholder = field.IsSecret
                    ? _localization.GetString("settings.secretPlaceholder")
                    : string.Empty;
            }
        }
    }

    private string BuildFieldDescription(UsageProviderSettingDescriptor field)
    {
        var description = _localization.GetString($"settings.field.{field.Key}.description", field.Description);
        return string.IsNullOrWhiteSpace(field.EnvironmentVariable)
            ? description
            : _localization.Format("settings.field.environmentVariable", description, field.EnvironmentVariable);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnSettingsChanged(object? sender, PaletteSettings args)
    {
        if (_disposed || _handlingSettingsChange)
        {
            return;
        }

        _handlingSettingsChange = true;
        var previousFingerprint = _providerConfigurationFingerprint;
        try
        {
            _localization.ApplyPreference(_language.Value);
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

        _providerConfigurationFingerprint = GetProviderConfigurationFingerprint();
        if (!string.Equals(previousFingerprint, _providerConfigurationFingerprint, StringComparison.Ordinal))
        {
            ProviderConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        RefreshLocalizedSettings();
        ValidateRefreshInterval();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string GetProviderConfigurationFingerprint()
    {
        // This comparison is intentionally process-local and never logged or persisted.
        // It lets us identify a key/account edit after secret values have been moved to DPAPI.
        var parts = new List<string>();
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            parts.Add(descriptor.Id);
            parts.Add(IsEnabled(descriptor.Id).ToString(CultureInfo.InvariantCulture));
            foreach (var field in descriptor.Settings)
            {
                parts.Add(field.Key);
                parts.Add(GetValue(descriptor.Id, field.Key) ?? string.Empty);
            }
        }
        return string.Join("\u001F", parts);
    }

    private bool ValidateRefreshInterval()
    {
        if (TryGetRefreshIntervalSeconds(out _))
        {
            _refreshInterval.ErrorMessage = string.Empty;
            return true;
        }

        _refreshInterval.ErrorMessage = _localization.Format(
            "settings.refreshIntervalError",
            MinimumRefreshIntervalSeconds,
            MaximumRefreshIntervalSeconds);
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

    internal sealed record SettingsStorage(string Directory, string SettingsPath, string SecretsPath);
}
