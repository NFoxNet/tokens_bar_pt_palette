using System;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TokensLimitsExtension.Settings;

internal sealed partial class GroupedCommandSettings : ICommandSettings, IDisposable
{
    private readonly NativeSettingsWindowHost? _host;
    private readonly GroupedSettingsPage _page;

    public GroupedCommandSettings(TokensLimitsSettings settings, Action? openWindow = null)
    {
        if (openWindow is null)
        {
            _host = new(settings);
            openWindow = _host.Open;
        }
        _page = new(settings, openWindow);
    }

    public IContentPage SettingsPage => _page;
    public void Dispose() { _page.Dispose(); _host?.Dispose(); }
}

internal sealed partial class GroupedSettingsPage : ContentPage, IDisposable
{
    private readonly TokensLimitsSettings _settings;
    private readonly Action _openWindow;

    public GroupedSettingsPage(TokensLimitsSettings settings, Action openWindow)
    {
        _settings = settings;
        _openWindow = openWindow;
        Id = "com.tokenslimits.settings";
        Icon = new IconInfo("\uE713");
        UpdateTitle();
        _settings.Changed += OnChanged;
    }

    public override IContent[] GetContent() => [new OpenSettingsForm(_settings, _openWindow)];
    public void Dispose() => _settings.Changed -= OnChanged;
    private void OnChanged(object? sender, EventArgs args) { UpdateTitle(); RaiseItemsChanged(); }
    private void UpdateTitle()
    {
        Name = _settings.Localization.GetString("settings.title");
        Title = _settings.Localization.GetString("app.title", "Tokens Limits");
    }

    private sealed partial class OpenSettingsForm : FormContent
    {
        private readonly Action _openWindow;
        public OpenSettingsForm(TokensLimitsSettings settings, Action openWindow)
        {
            _openWindow = openWindow;
            TemplateJson = new JsonObject
            {
                ["type"] = "AdaptiveCard", ["version"] = "1.5",
                ["body"] = new JsonArray(new JsonObject
                {
                    ["type"] = "TextBlock", ["text"] = settings.Localization.GetString("settings.window.description"), ["wrap"] = true,
                }),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["type"] = "Action.Submit", ["associatedInputs"] = "none",
                    ["title"] = settings.Localization.GetString("settings.window.open"),
                    ["data"] = new JsonObject { ["openSettings"] = true },
                }),
            }.ToJsonString();
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            if (JsonNode.Parse(data)?["openSettings"]?.GetValue<bool>() != true)
                throw new ArgumentException("Unknown settings action.", nameof(data));
            _openWindow();
            return CommandResult.KeepOpen();
        }
    }
}
