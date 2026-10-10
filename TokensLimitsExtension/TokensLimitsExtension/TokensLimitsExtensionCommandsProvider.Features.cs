using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Notifications;

namespace TokensLimitsExtension;

public partial class TokensLimitsExtensionCommandsProvider
{
    private readonly ProviderSetupPage _setupPage;
    private UsageNotificationController? _notificationController;

    private void UpdateFeatureControllers(IReadOnlyList<UsageSnapshotCache> caches)
    {
        if (_settings.NotificationsEnabled)
        {
            if (_notificationController is null)
            {
                var segment = System.Guid.NewGuid().ToString("N");
                _notificationController = new UsageNotificationController(
                    caches, new UsageNotificationEvaluator(),
                    new CommandPaletteUsageNotificationDelivery(_settings.Localization),
                    _ => segment, thresholdPercent: _settings.NotificationThresholdPercent);
                _notificationController.DeliveryFailed += NotificationOnDeliveryFailed;
            }
            else
            {
                _notificationController.ThresholdPercent = _settings.NotificationThresholdPercent;
                _notificationController.UpdateProviders(caches);
            }
        }
        else if (_notificationController is not null)
        {
            _notificationController.DeliveryFailed -= NotificationOnDeliveryFailed;
            _notificationController.Dispose();
            _notificationController = null;
        }
        UpdateFeatureNavigation();
    }

    private void UpdateFeatureNavigation()
    {
        IContextItem[] navigation =
        [
            new CommandContextItem(_setupPage) { Title = _settings.Localization.GetString("setup.name") },
            new CommandContextItem(_settings.Settings.SettingsPage) { Title = _settings.Localization.GetString("setup.action.settings") },
        ];
        ((CommandItem)_commands[0]).MoreCommands = navigation;
        _overviewPage.UpdateNavigationCommands(navigation);
    }

    private void NotificationOnDeliveryFailed(object? sender, UsageNotificationDeliveryFailedEventArgs args)
        => LogMessage($"Notification delivery failed for {args.ProviderId}: {args.ErrorType}");

    private void DisposeFeatureControllers()
    {
        _setupPage.Dispose();
        if (_notificationController is not null)
        {
            _notificationController.DeliveryFailed -= NotificationOnDeliveryFailed;
            _notificationController.Dispose();
            _notificationController = null;
        }
    }
}
