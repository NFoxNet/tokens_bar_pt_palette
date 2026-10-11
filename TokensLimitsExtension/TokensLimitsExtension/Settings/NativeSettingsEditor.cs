using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Settings;

internal sealed class NativeSettingsEditor
{
    private static readonly string[] GeneralKeys = ["tokensLimits.language", "tokensLimits.refreshInterval"];
    private static readonly string[] DockKeys = [
        "tokensLimits.dock.summaryEnabled", "tokensLimits.dock.metric", "tokensLimits.dock.providerOrder",
    ];
    private static readonly string[] NotificationKeys = ["tokensLimits.notifications.enabled", "tokensLimits.notifications.threshold"];
    private static readonly string[] HistoryKeys = ["tokensLimits.history.enabled", "tokensLimits.history.retentionDays"];

    private readonly TokensLimitsSettings _settings;
    private readonly HashSet<string> _secretKeys;

    public NativeSettingsEditor(TokensLimitsSettings settings)
    {
        _settings = settings;
        _secretKeys = UsageProviderDescriptorRegistry.All
            .SelectMany(descriptor => descriptor.Settings
                .Where(field => field.IsSecret)
                .Select(field => $"tokensLimits.providers.{descriptor.Id}.{field.Key}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> KeysForSection(string section) => section switch
    {
        "general" => GeneralKeys,
        "providers" => UsageProviderDescriptorRegistry.All.SelectMany(ProviderKeys).ToArray(),
        "dock" => DockKeys.Concat(_settings.EnabledDescriptors.Select(VisibilityKey)).ToArray(),
        "notifications" => NotificationKeys,
        "history" => HistoryKeys,
        _ => throw new ArgumentException("Unknown settings section.", nameof(section)),
    };

    public static IReadOnlyList<string> KeysForProvider(string providerId)
    {
        var descriptor = UsageProviderDescriptorRegistry.All.FirstOrDefault(
            item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        return descriptor is null
            ? throw new ArgumentException("Unknown provider.", nameof(providerId))
            : ProviderKeys(descriptor).ToArray();
    }

    public IReadOnlyList<NativeSettingField> Fields(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var template = NativeForm().TemplateJson;
        var inputs = ParseInputs(template);
        return keys.Select(key =>
        {
            if (!inputs.TryGetValue(key, out var row))
            {
                throw new ArgumentException($"Unknown setting '{key}'.", nameof(keys));
            }

            var input = row.Input;
            var text = DescendantText(row.Card).ToArray();
            var title = input["title"]?.GetValue<string>();
            var isSecret = _secretKeys.Contains(key);
            var hasSavedSecret = isSecret && _settings.HasSavedSecret(key);
            var choices = input["choices"] is JsonArray choiceArray
                ? choiceArray.Select(choice => new NativeSettingChoice(
                    choice!["value"]?.GetValue<string>() ?? string.Empty,
                    choice["title"]?.GetValue<string>() ?? string.Empty)).ToArray()
                : [];
            return new NativeSettingField(
                key,
                string.IsNullOrWhiteSpace(title) ? text.FirstOrDefault() ?? string.Empty : title,
                input["label"]?.GetValue<string>() ?? text.Skip(1).FirstOrDefault() ?? string.Empty,
                input["type"]?.GetValue<string>() ?? string.Empty,
                hasSavedSecret ? TokensLimitsSettings.SecretMask : isSecret ? string.Empty : input["value"]?.GetValue<string>() ?? string.Empty,
                isSecret && !hasSavedSecret ? string.Empty : input["placeholder"]?.GetValue<string>() ?? string.Empty,
                input["isRequired"]?.GetValue<bool>() ?? false,
                isSecret,
                choices,
                input["errorMessage"]?.GetValue<string>() ?? string.Empty);
        }).ToArray();
    }

    public IReadOnlyDictionary<string, string> Save(IEnumerable<string> keys, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        var scopedKeys = keys.ToHashSet(StringComparer.Ordinal);
        if (scopedKeys.Count == 0 || values.Keys.Any(key => !scopedKeys.Contains(key)))
        {
            throw new ArgumentException("Values must belong to a non-empty settings scope.", nameof(values));
        }

        var registered = ParseInputs(NativeForm().TemplateJson);
        if (scopedKeys.Any(key => !registered.ContainsKey(key)) || values.Keys.Any(key => !registered.ContainsKey(key)))
        {
            throw new ArgumentException("The settings scope contains an unknown key.", nameof(keys));
        }

        var inputs = new JsonObject();
        var data = new JsonObject();
        foreach (var (key, value) in values)
        {
            inputs.Add(key, value);
            data.Add(key, key);
        }

        NativeForm().SubmitForm(inputs.ToJsonString(), data.ToJsonString());
        return Fields(scopedKeys)
            .Where(field => !string.IsNullOrWhiteSpace(field.ErrorMessage))
            .ToDictionary(field => field.Key, field => field.ErrorMessage, StringComparer.Ordinal);
    }

    private SettingsForm NativeForm() => (SettingsForm)_settings.Settings.ToContent().Single();

    private static Dictionary<string, (JsonObject Input, JsonObject Card)> ParseInputs(string template)
    {
        var card = JsonNode.Parse(template)!.AsObject();
        var inputs = new Dictionary<string, (JsonObject, JsonObject)>(StringComparer.Ordinal);
        foreach (var row in card["body"]!.AsArray().OfType<JsonObject>())
        {
            Visit(row, row);
        }
        return inputs;

        void Visit(JsonNode? node, JsonObject row)
        {
            if (node is JsonObject obj)
            {
                if (obj["type"]?.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal) == true)
                {
                    inputs.Add(obj["id"]!.GetValue<string>(), (obj, row));
                    return;
                }

                foreach (var child in obj.Select(pair => pair.Value))
                {
                    Visit(child, row);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    Visit(child, row);
                }
            }
        }
    }

    private static IEnumerable<string> DescendantText(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "TextBlock" && obj["text"] is JsonValue text)
            {
                yield return text.GetValue<string>();
                yield break;
            }
            foreach (var child in obj.Select(pair => pair.Value))
            foreach (var value in DescendantText(child))
                yield return value;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            foreach (var value in DescendantText(child))
                yield return value;
        }
    }

    private static IEnumerable<string> ProviderKeys(UsageProviderDescriptor descriptor)
    {
        var prefix = $"tokensLimits.providers.{descriptor.Id}.";
        return [prefix + "enabled", .. descriptor.Settings.Select(field => prefix + field.Key)];
    }

    private static string VisibilityKey(UsageProviderDescriptor descriptor)
        => $"tokensLimits.dock.providers.{descriptor.Id}.visible";
}

internal sealed record NativeSettingField(
    string Key,
    string Title,
    string Description,
    string Type,
    string Value,
    string Placeholder,
    bool IsRequired,
    bool IsSecret,
    IReadOnlyList<NativeSettingChoice> Choices,
    string ErrorMessage);

internal sealed record NativeSettingChoice(string Value, string Title);
