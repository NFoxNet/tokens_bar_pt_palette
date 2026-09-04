using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Localization;

public sealed partial class JsonLocalizationService : ILocalizationService
{
    private const string DefaultCulture = "en";
    private readonly IReadOnlyDictionary<string, LanguagePack> _packs;
    private string _preference;
    private LanguagePack _current;

    public JsonLocalizationService(string packagedDirectory, string userDirectory, string? preference = null)
    {
        _packs = LoadPacks(packagedDirectory, userDirectory);
        if (!_packs.TryGetValue(DefaultCulture, out var english))
        {
            throw new InvalidOperationException("The required English language pack is missing.");
        }

        _current = english;
        _preference = preference ?? "auto";
        ApplyPreference(_preference, notify: false);
    }

    public event EventHandler? LanguageChanged;

    // UI language and regional formatting are deliberately separate. A user may
    // choose English labels while retaining the Windows decimal/date conventions.
    public CultureInfo Culture => CultureInfo.CurrentCulture;

    public IReadOnlyList<LanguageOption> Languages => _packs.Values
        .OrderBy(pack => pack.Culture, StringComparer.OrdinalIgnoreCase)
        .Select(pack => new LanguageOption(pack.Culture, pack.NativeName))
        .ToArray();

    public string Preference => _preference;

    public string GetString(string key, string? fallback = null)
    {
        if (_current.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_packs.TryGetValue(DefaultCulture, out var english)
            && english.Strings.TryGetValue(key, out value))
        {
            return value;
        }

        return fallback ?? key;
    }

    public string Format(string key, params object?[] arguments)
        => string.Format(Culture, GetString(key), arguments);

    public void ApplyPreference(string? preference, bool notify = true)
    {
        _preference = string.IsNullOrWhiteSpace(preference) ? "auto" : preference.Trim();
        var next = Resolve(_preference);
        if (string.Equals(next.Culture, _current.Culture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current = next;
        if (notify)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private LanguagePack Resolve(string preference)
    {
        var requested = preference.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.CurrentUICulture.Name
            : preference;
        while (!string.IsNullOrWhiteSpace(requested))
        {
            if (_packs.TryGetValue(requested, out var exact))
            {
                return exact;
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(requested);
                requested = culture.Parent.Name;
            }
            catch (CultureNotFoundException)
            {
                requested = string.Empty;
            }
        }

        return _packs[DefaultCulture];
    }

    private static Dictionary<string, LanguagePack> LoadPacks(string packagedDirectory, string userDirectory)
    {
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        LoadDirectory(packagedDirectory, packs, required: true);
        LoadDirectory(userDirectory, packs, required: false);
        return packs;
    }

    private static void LoadDirectory(string directory, IDictionary<string, LanguagePack> packs, bool required)
    {
        if (!Directory.Exists(directory))
        {
            if (required)
            {
                throw new DirectoryNotFoundException($"Language directory is missing: {directory}");
            }

            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var source = File.ReadAllText(path);
                var file = JsonSerializer.Deserialize(source, LanguageJsonContext.Default.LanguageFile)
                    ?? throw new JsonException("Language file is empty.");
                if (string.IsNullOrWhiteSpace(file.Culture) || string.IsNullOrWhiteSpace(file.NativeName))
                {
                    throw new JsonException("Language file must contain culture and nativeName.");
                }

                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                if (packs.TryGetValue(file.Culture, out var existing))
                {
                    foreach (var pair in existing.Strings)
                    {
                        strings[pair.Key] = pair.Value;
                    }
                }

                foreach (var pair in file.Strings ?? [])
                {
                    strings[pair.Key] = pair.Value;
                }

                packs[file.Culture] = new LanguagePack(
                    file.Culture,
                    file.NativeName,
                    CultureInfo.GetCultureInfo(file.Culture),
                    strings);
            }
            catch (Exception) when (!required)
            {
                // User-supplied language files must not prevent the extension from starting.
            }
        }
    }

    private sealed record LanguageFile(string Culture, string NativeName, Dictionary<string, string>? Strings);

    private sealed record LanguagePack(
        string Culture,
        string NativeName,
        CultureInfo CultureInfo,
        IReadOnlyDictionary<string, string> Strings);

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(LanguageFile))]
    private sealed partial class LanguageJsonContext : JsonSerializerContext
    {
    }
}

public sealed record LanguageOption(string Culture, string NativeName);
