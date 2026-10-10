using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TokensLimitsExtension.Settings;

public sealed partial class TokensLimitsSettings
{
    private readonly ToggleSetting _notificationsEnabled = new("tokensLimits.notifications.enabled", string.Empty, string.Empty, false);
    private readonly TextSetting _notificationThreshold = new("tokensLimits.notifications.threshold", string.Empty, string.Empty, "10");
    private readonly ToggleSetting _historyEnabled = new("tokensLimits.history.enabled", string.Empty, string.Empty, false);
    private readonly ChoiceSetSetting _historyRetention = new("tokensLimits.history.retentionDays", [new("14 days", "14")]);
    private int _notificationThresholdPercent = 10;
    private int _historyRetentionDays = 14;

    public bool NotificationsEnabled => _notificationsEnabled.Value;
    public int NotificationThresholdPercent => _notificationThresholdPercent;
    public bool HistoryEnabled => _historyEnabled.Value;
    public int HistoryRetentionDays => _historyRetentionDays;
    internal string HistoryStorageDirectory => Path.Combine(Path.GetDirectoryName(FilePath)!, "history");

    private void AddFeatureSettings()
    {
        Settings.Add(_notificationsEnabled);
        Settings.Add(_notificationThreshold);
        Settings.Add(_historyEnabled);
        Settings.Add(_historyRetention);
    }

    private void RefreshFeatureSettings()
    {
        _notificationsEnabled.Label = _localization.GetString("settings.notificationsEnabled");
        _notificationsEnabled.Description = _localization.GetString("settings.notificationsDescription");
        _notificationThreshold.Label = _localization.GetString("settings.notificationThreshold");
        _notificationThreshold.Description = _localization.GetString("settings.notificationThresholdDescription");
        _historyEnabled.Label = _localization.GetString("settings.historyEnabled");
        _historyEnabled.Description = _localization.GetString("settings.historyDescription");
        _historyRetention.Label = _localization.GetString("settings.historyRetention");
        _historyRetention.Description = _localization.GetString("settings.historyRetentionDescription");
        _historyRetention.Choices =
        [
            new(_localization.Format("settings.historyDays", 7), "7"),
            new(_localization.Format("settings.historyDays", 14), "14"),
            new(_localization.Format("settings.historyDays", 30), "30"),
        ];
        ValidateFeatureSettings();
    }

    private void ValidateFeatureSettings()
    {
        if (int.TryParse(_notificationThreshold.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            && threshold is >= 1 and <= 99)
        {
            _notificationThresholdPercent = threshold;
            _notificationThreshold.ErrorMessage = string.Empty;
        }
        else
        {
            _notificationThreshold.ErrorMessage = _localization.GetString("settings.notificationThresholdError");
        }

        if (int.TryParse(_historyRetention.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention)
            && retention is 7 or 14 or 30)
        {
            _historyRetentionDays = retention;
            _historyRetention.Description = _localization.GetString("settings.historyRetentionDescription");
        }
        else
        {
            _historyRetention.Description = _localization.GetString("settings.historyRetentionError");
        }
    }
}


