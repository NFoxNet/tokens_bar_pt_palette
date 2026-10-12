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
    private UsageHistoryStore? _historyStore;
    private UsageHistoryController? _historyController;
    private UsageHistoryPage? _historyPage;

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
        if (_settings.HistoryEnabled)
        {
            if (_historyController is null)
            {
                _historyStore = new UsageHistoryStore(_settings.HistoryStorageDirectory, _settings.HistoryRetentionDays);
                _historyController = new UsageHistoryController(_historyStore, caches);
                _historyPage = new UsageHistoryPage(_historyController, caches, _settings.Localization);
            }
            else
            {
                _historyStore!.SetRetentionDays(_settings.HistoryRetentionDays);
                _historyPage!.UpdateProviders(caches);
            }
        }
        else
        {
            DisposeHistory();
        }
        UpdateFeatureNavigation();
    }

    private void UpdateFeatureNavigation()
    {
        IContextItem[] navigation =
        [
            new CommandContextItem(_setupPage) { Title = _settings.Localization.GetString("setup.name") },
            new CommandContextItem(_settingsPageCommand) { Title = _settings.Localization.GetString("setup.action.settings") },
        ];
        if (_historyPage is not null)
        {
            navigation = [.. navigation, new CommandContextItem(_historyPage) { Title = _settings.Localization.GetString("history.title") }];
        }
        ((CommandItem)_commands[0]).MoreCommands = navigation;
        _overviewPage.UpdateNavigationCommands(navigation);
    }

    private void NotificationOnDeliveryFailed(object? sender, UsageNotificationDeliveryFailedEventArgs args)
        => LogMessage($"Notification delivery failed for {args.ProviderId}: {args.ErrorType}");

    private void DisposeHistory()
    {
        _historyPage?.Dispose();
        _historyPage = null;
        _historyController?.Dispose();
        _historyController = null;
        _historyStore = null;
    }

    private void DisposeFeatureControllers()
    {
        _setupPage.Dispose();
        DisposeHistory();
        if (_notificationController is not null)
        {
            _notificationController.DeliveryFailed -= NotificationOnDeliveryFailed;
            _notificationController.Dispose();
            _notificationController = null;
        }
    }
}
