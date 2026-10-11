using System;
using System.Collections.Generic;
using System.IO;
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
    private string? _expandedSection;
    private string? _expandedProvider;

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

        JsonObject Fields(IEnumerable<string> keys)
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
            body.Add((JsonNode)new JsonObject
            {
                ["type"] = "ActionSet", ["actions"] = new JsonArray(action),
            });
            return new JsonObject { ["type"] = "Container", ["spacing"] = "medium", ["items"] = body };
        }

        JsonObject Section(string id, string title, string description, JsonObject icon,
            Func<JsonObject> content, string? status = null, bool enabled = false, bool provider = false)
        {
            var expanded = provider ? _expandedProvider == id : _expandedSection == id;
            var columns = new JsonArray(
                new JsonObject
                {
                    ["type"] = "Column", ["width"] = "auto", ["verticalContentAlignment"] = "center",
                    ["items"] = new JsonArray(icon),
                },
                new JsonObject
                {
                    ["type"] = "Column", ["width"] = "stretch", ["verticalContentAlignment"] = "center",
                    ["items"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "TextBlock", ["text"] = title, ["size"] = "medium",
                            ["weight"] = "bolder", ["wrap"] = true,
                        },
                        new JsonObject
                        {
                            ["type"] = "TextBlock", ["text"] = description,
                            ["isSubtle"] = true, ["wrap"] = true, ["spacing"] = "small",
                        }),
                });
            if (status is not null)
            {
                columns.Add((JsonNode)new JsonObject
                {
                    ["type"] = "Column", ["width"] = "auto", ["verticalContentAlignment"] = "center",
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "TextBlock", ["text"] = status, ["wrap"] = true,
                        ["color"] = enabled ? "good" : "default", ["isSubtle"] = !enabled,
                    }),
                });
            }
            columns.Add((JsonNode)new JsonObject
            {
                ["type"] = "Column", ["width"] = "auto", ["verticalContentAlignment"] = "center",
                ["items"] = new JsonArray(new JsonObject
                {
                    ["type"] = "TextBlock", ["text"] = expanded ? "⌃" : "⌄", ["size"] = "large",
                }),
            });
            var items = new JsonArray(new JsonObject
            {
                ["type"] = "Container",
                ["selectAction"] = new JsonObject
                {
                    ["type"] = "Action.Submit", ["associatedInputs"] = "none",
                    ["title"] = _settings.Localization.Format("settings.card.configureTitle", title),
                    ["tooltip"] = _settings.Localization.Format("settings.card.configureTitle", title),
                    ["data"] = new JsonObject { [provider ? "settingsProvider" : "settingsSection"] = id },
                },
                ["items"] = new JsonArray(new JsonObject { ["type"] = "ColumnSet", ["columns"] = columns }),
            });
            if (expanded) items.Add((JsonNode)content());
            return new JsonObject
            {
                ["type"] = "Container", ["style"] = "emphasis", ["spacing"] = "medium", ["items"] = items,
            };
        }

        static JsonObject Symbol(string text) => new()
        {
            ["type"] = "TextBlock", ["text"] = text, ["size"] = "large", ["spacing"] = "none",
        };
        string State(bool enabled) => _settings.Localization.GetString(enabled ? "settings.card.on" : "settings.card.off");
        JsonObject Providers()
        {
            var items = new JsonArray();
            foreach (var descriptor in UsageProviderDescriptorRegistry.All)
            {
                var prefix = $"tokensLimits.providers.{descriptor.Id}.";
                var keys = new[] { prefix + "enabled" }.Concat(descriptor.Settings.Select(field => prefix + field.Key));
                var icon = new JsonObject
                {
                    ["type"] = "Image", ["width"] = "32px", ["height"] = "32px", ["altText"] = descriptor.DisplayName,
                    ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(
                        Path.Combine(AppContext.BaseDirectory, ProviderIconCatalog.PathFor(descriptor.Id)))),
                };
                items.Add((JsonNode)Section(descriptor.Id, descriptor.DisplayName,
                    _settings.Localization.GetString("settings.card.providerDescription"), icon,
                    () => Fields(keys), State(_settings.IsEnabled(descriptor.Id)), _settings.IsEnabled(descriptor.Id), provider: true));
            }
            return new JsonObject { ["type"] = "Container", ["spacing"] = "medium", ["items"] = items };
        }
        var dockKeys = DockKeys
            .Concat(_settings.EnabledDescriptors.Select(descriptor => $"tokensLimits.dock.providers.{descriptor.Id}.visible"));
        var notificationKeys = new List<string> { "tokensLimits.notifications.enabled" };
        if (_settings.NotificationsEnabled) notificationKeys.Add("tokensLimits.notifications.threshold");
        var historyKeys = new List<string> { "tokensLimits.history.enabled" };
        if (_settings.HistoryEnabled) historyKeys.Add("tokensLimits.history.retentionDays");

        original["body"] = new JsonArray(
            Section("general", _settings.Localization.GetString("settings.section.general"), _settings.Localization.GetString("settings.card.generalDescription"), Symbol("⚙"), () => Fields(GeneralKeys)),
            Section("providers", _settings.Localization.GetString("settings.section.providers"), _settings.Localization.GetString("settings.card.providersDescription"), Symbol("⊞"), Providers, _settings.Localization.Format("settings.card.enabledCount", _settings.EnabledDescriptors.Count)),
            Section("dock", _settings.Localization.GetString("settings.section.dock"), _settings.Localization.GetString("settings.card.dockDescription"), Symbol("▤"), () => Fields(dockKeys)),
            Section("notifications", _settings.Localization.GetString("settings.section.notifications"), _settings.Localization.GetString("settings.card.notificationsDescription"), Symbol("♢"), () => Fields(notificationKeys), State(_settings.NotificationsEnabled), _settings.NotificationsEnabled),
            Section("history", _settings.Localization.GetString("settings.section.history"), _settings.Localization.GetString("settings.card.historyDescription"), Symbol("◷"), () => Fields(historyKeys), State(_settings.HistoryEnabled), _settings.HistoryEnabled));
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
            var action = string.IsNullOrEmpty(data) ? null : JsonNode.Parse(data)?.AsObject();
            if (action?["settingsSection"] is JsonValue sectionValue)
            {
                var section = sectionValue.GetValue<string>();
                if (section is not ("general" or "providers" or "dock" or "notifications" or "history"))
                    throw new ArgumentException("Unknown settings section.", nameof(data));
                _page._expandedSection = _page._expandedSection == section ? null : section;
                _page._expandedProvider = null;
            }
            else if (action?["settingsProvider"] is JsonValue providerValue)
            {
                var provider = providerValue.GetValue<string>();
                if (_page._expandedSection != "providers" || !UsageProviderDescriptorRegistry.All.Any(descriptor => descriptor.Id == provider))
                    throw new ArgumentException("Unknown or unavailable provider settings.", nameof(data));
                _page._expandedProvider = _page._expandedProvider == provider ? null : provider;
            }
            else
            {
                _native.SubmitForm(inputs, data);
            }
            TemplateJson = _page.GroupTemplate(_page.NativeForm().TemplateJson);
            return CommandResult.KeepOpen();
        }
    }
}
