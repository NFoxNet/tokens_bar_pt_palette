using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Settings;

internal sealed partial class GroupedCommandSettings : ICommandSettings, IDisposable
{
    private readonly GroupedSettingsPage _page;
    public GroupedCommandSettings(TokensLimitsSettings settings) => _page = new(settings);
    public IContentPage SettingsPage => _page;
    public void Dispose() => _page.Dispose();
}

internal sealed partial class GroupedSettingsPage : ContentPage, IDisposable
{
    private static readonly string[] GeneralKeys = ["tokensLimits.language", "tokensLimits.refreshInterval"];
    private static readonly string[] DockKeys = ["tokensLimits.dock.summaryEnabled", "tokensLimits.dock.metric", "tokensLimits.dock.providerOrder"];
    private readonly TokensLimitsSettings _settings;

    public GroupedSettingsPage(TokensLimitsSettings settings)
    {
        _settings = settings;
        Id = "com.tokenslimits.settings";
        Icon = new IconInfo("\uE713");
        UpdateTitle();
        _settings.Changed += OnChanged;
    }

    public override IContent[] GetContent() => [new GroupedSettingsForm(this)];

    public void Dispose() => _settings.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs args)
    {
        UpdateTitle();
        RaiseItemsChanged();
    }

    private void UpdateTitle()
    {
        Name = _settings.Localization.GetString("settings.title");
        Title = _settings.Localization.GetString("app.title", "Tokens Limits");
    }

    private SettingsForm NativeForm() => (SettingsForm)_settings.Settings.ToContent().Single();

    private string GroupTemplate(string template)
    {
        var original = JsonNode.Parse(template)!.AsObject();
        var inputs = original["body"]!.AsArray().ToDictionary(node => InputId(node!), node => node!);
        var submit = original["actions"]![0]!.AsObject();

        JsonObject Card(IEnumerable<string> keys)
        {
            var body = new JsonArray();
            var data = new JsonObject();
            foreach (var key in keys)
            {
                body.Add(inputs[key].DeepClone());
                data.Add(key, submit["data"]![key]!.DeepClone());
            }
            var action = submit.DeepClone().AsObject();
            action["title"] = _settings.Localization.GetString("settings.save");
            action["data"] = data;
            return new JsonObject
            {
                ["type"] = "AdaptiveCard",
                ["version"] = "1.5",
                ["body"] = body,
                ["actions"] = new JsonArray(action),
            };
        }

        static JsonObject Section(string title, JsonObject card) => new()
        {
            ["type"] = "ActionSet",
            ["actions"] = new JsonArray(new JsonObject
            {
                ["type"] = "Action.ShowCard",
                ["title"] = title,
                ["card"] = card,
            }),
        };

        var providers = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.5", ["body"] = new JsonArray() };
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var prefix = $"tokensLimits.providers.{descriptor.Id}.";
            var keys = new[] { prefix + "enabled" }.Concat(descriptor.Settings.Select(field => prefix + field.Key));
            providers["body"]!.AsArray().Add((JsonNode)Section(descriptor.DisplayName, Card(keys)));
        }

        var dockKeys = DockKeys
            .Concat(_settings.EnabledDescriptors.Select(descriptor => $"tokensLimits.dock.providers.{descriptor.Id}.visible"));
        var notificationKeys = new List<string> { "tokensLimits.notifications.enabled" };
        if (_settings.NotificationsEnabled) notificationKeys.Add("tokensLimits.notifications.threshold");
        var historyKeys = new List<string> { "tokensLimits.history.enabled" };
        if (_settings.HistoryEnabled) historyKeys.Add("tokensLimits.history.retentionDays");

        original["body"] = new JsonArray(
            Section(_settings.Localization.GetString("settings.section.general"), Card(GeneralKeys)),
            Section(_settings.Localization.GetString("settings.section.providers"), providers),
            Section(_settings.Localization.GetString("settings.section.dock"), Card(dockKeys)),
            Section(_settings.Localization.GetString("settings.section.notifications"), Card(notificationKeys)),
            Section(_settings.Localization.GetString("settings.section.history"), Card(historyKeys)));
        original["actions"] = new JsonArray();
        return original.ToJsonString();
    }

    private static string InputId(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal))
                return obj["id"]!.GetValue<string>();
            return obj.SelectMany(pair => DescendantInputs(pair.Value)).Single();
        }
        throw new InvalidOperationException("A settings row must contain exactly one input.");
    }

    private static IEnumerable<string> DescendantInputs(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal))
                return [obj["id"]!.GetValue<string>()];
            return obj.SelectMany(pair => DescendantInputs(pair.Value));
        }
        return node is JsonArray array ? array.SelectMany(DescendantInputs) : [];
    }

    private sealed partial class GroupedSettingsForm : FormContent
    {
        private readonly GroupedSettingsPage _page;
        private readonly SettingsForm _native;

        public GroupedSettingsForm(GroupedSettingsPage page)
        {
            _page = page;
            _native = page.NativeForm();
            TemplateJson = page.GroupTemplate(_native.TemplateJson);
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            _native.SubmitForm(inputs, data);
            TemplateJson = _page.GroupTemplate(_page.NativeForm().TemplateJson);
            return CommandResult.KeepOpen();
        }
    }
}
