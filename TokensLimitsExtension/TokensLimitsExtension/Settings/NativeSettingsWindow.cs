using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TokensLimitsExtension.Core.Providers;
using Windows.Graphics;

namespace TokensLimitsExtension.Settings;

internal sealed partial class NativeSettingsWindow : Window
{
    private readonly TokensLimitsSettings _settings;
    private readonly NativeSettingsEditor _editor;
    private readonly List<Action> _localize = [];
    private volatile bool _closed;

    public NativeSettingsWindow(TokensLimitsSettings settings)
    {
        _settings = settings;
        _editor = new(settings);
        Title = settings.Localization.GetString("settings.title") + " — Tokens Limits";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1000, 780));
        var panel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(24),
            ChildrenTransitions = new TransitionCollection
            {
                new EntranceThemeTransition(), new RepositionThemeTransition { IsStaggeringEnabled = false },
            },
        };
        var title = new TextBlock { FontSize = 28, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(title);
        _localize.Add(() => title.Text = _settings.Localization.GetString("settings.title"));
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        panel.Children.Add(hint);
        _localize.Add(() => hint.Text = _settings.Localization.GetString("settings.window.instructions"));
        AddSection(panel, "general", "\uE713", () => _editor.KeysForSection("general"));
        var providers = Expander("providers", "\uE716");
        providers.ItemContainerStyleSelector = new SettingsExpanderItemStyleSelector
        {
            DefaultStyle = (Style)Application.Current.Resources["DefaultSettingsExpanderStyle"],
        };
        providers.Expanded += (_, _) =>
        {
            if (providers.Items.Count != 0) return;
            foreach (var descriptor in UsageProviderDescriptorRegistry.All)
            {
                var provider = new SettingsExpander
                {
                    Header = descriptor.DisplayName,
                    Items = new ObservableCollection<object>(),
                    HeaderIcon = new BitmapIcon
                    {
                        UriSource = new Uri("ms-appx:///" + ProviderIconCatalog.PathFor(descriptor.Id).Replace('\\', '/')),
                        ShowAsMonochrome = false,
                    },
                };
                _localize.Add(() => provider.Description = _settings.Localization.GetString("settings.card.providerDescription"));
                provider.Description = _settings.Localization.GetString("settings.card.providerDescription");
                AddFieldsOnExpansion(provider, () => NativeSettingsEditor.KeysForProvider(descriptor.Id));
                providers.Items.Add(provider);
            }
        };
        panel.Children.Add(providers);
        AddSection(panel, "dock", "\uE8A0", () => _editor.KeysForSection("dock"));
        AddSection(panel, "notifications", "\uE7F4", () => _editor.KeysForSection("notifications"));
        AddSection(panel, "history", "\uE81C", () => _editor.KeysForSection("history"));
        Content = new ScrollViewer
        {
            Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        foreach (var localize in _localize) localize();
        _settings.Changed += SettingsChanged;
        Closed += (_, _) => { _closed = true; _settings.Changed -= SettingsChanged; _localize.Clear(); };
    }

    private SettingsExpander Expander(string id, string glyph)
    {
        var expander = new SettingsExpander { HeaderIcon = new FontIcon { Glyph = glyph }, Items = new ObservableCollection<object>() };
        _localize.Add(() =>
        {
            expander.Header = _settings.Localization.GetString("settings.section." + id);
            expander.Description = _settings.Localization.GetString("settings.card." + id + "Description");
        });
        return expander;
    }

    private void AddSection(StackPanel panel, string id, string glyph, Func<IReadOnlyList<string>> keys)
    {
        var expander = Expander(id, glyph);
        AddFieldsOnExpansion(expander, keys);
        panel.Children.Add(expander);
    }

    private void AddFieldsOnExpansion(SettingsExpander expander, Func<IReadOnlyList<string>> keys)
    {
        IReadOnlyList<string>? renderedKeys = null;
        var values = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        var setters = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
        var controls = new Dictionary<string, Control>(StringComparer.Ordinal);
        var feedback = new InfoBar { IsClosable = false };
        var save = new Button();
        _localize.Add(() => save.Content = _settings.Localization.GetString("settings.save"));
        save.Content = _settings.Localization.GetString("settings.save");
        var footer = new StackPanel { Spacing = 8, Children = { save, feedback } };
        expander.Expanded += (_, _) =>
        {
            var currentKeys = keys();
            if (renderedKeys is not null && renderedKeys.SequenceEqual(currentKeys)) return;
            var drafts = values.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);
            expander.Items.Clear();
            values.Clear();
            setters.Clear();
            controls.Clear();
            renderedKeys = currentKeys;
            foreach (var field in _editor.Fields(currentKeys))
            {
                var value = drafts.TryGetValue(field.Key, out var draft) ? draft : field.Value;
                Control input;
                if (field.IsSecret)
                {
                    var password = new PasswordBox { Password = value, PlaceholderText = field.Placeholder, PasswordRevealMode = PasswordRevealMode.Hidden };
                    input = password;
                    values[field.Key] = () => password.Password;
                    setters[field.Key] = text => password.Password = text;
                }
                else if (field.Type == "Input.Toggle")
                {
                    var toggle = new ToggleSwitch { IsOn = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) };
                    input = toggle;
                    values[field.Key] = () => toggle.IsOn ? "true" : "false";
                    setters[field.Key] = text => toggle.IsOn = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (field.Type == "Input.ChoiceSet")
                {
                    var choice = new ComboBox();
                    SetChoices(choice, field.Choices, value);
                    input = choice;
                    values[field.Key] = () => (choice.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                    setters[field.Key] = text => choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == text);
                }
                else if (field.Type == "Input.Text")
                {
                    var text = new TextBox { Text = value, PlaceholderText = field.Placeholder };
                    input = text;
                    values[field.Key] = () => text.Text;
                    setters[field.Key] = value => text.Text = value;
                }
                else throw new InvalidOperationException("Unsupported native settings input: " + field.Type);
                controls[field.Key] = input;
                if (input is not ToggleSwitch)
                {
                    input.MinWidth = 180;
                    input.MaxWidth = 280;
                    input.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                AutomationProperties.SetName(input, field.Title);
                var row = new SettingsCard { Header = field.Title, Description = field.Description, Content = input };
                expander.Items.Add(row);
            }
            foreach (var toggle in controls.Values.OfType<ToggleSwitch>())
                toggle.Toggled += (_, _) => UpdateEnabled();
            UpdateEnabled();
            expander.ItemsFooter = footer;
        };
        void UpdateEnabled()
        {
            foreach (var (key, control) in controls)
            {
                var enabledKey = key switch
                {
                    "tokensLimits.notifications.threshold" => "tokensLimits.notifications.enabled",
                    "tokensLimits.history.retentionDays" => "tokensLimits.history.enabled",
                    _ => null,
                };
                if (enabledKey is not null)
                    control.IsEnabled = string.Equals(values[enabledKey](), "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        _localize.Add(() =>
        {
            if (renderedKeys is null) return;
            var fields = _editor.Fields(renderedKeys);
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var row = (SettingsCard)expander.Items[index];
                row.Header = field.Title;
                row.Description = field.Description;
                AutomationProperties.SetName(controls[field.Key], field.Title);
                if (controls[field.Key] is ComboBox choice)
                {
                    var selected = values[field.Key]();
                    SetChoices(choice, field.Choices, selected);
                }
            }
        });
        save.Click += (_, _) =>
            {
                try
                {
                    var currentKeys = renderedKeys ?? throw new InvalidOperationException("The settings section is not open.");
                    var errors = _editor.Save(currentKeys, values.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal));
                    foreach (var field in _editor.Fields(currentKeys))
                        if (field.IsSecret || errors.Count == 0) setters[field.Key](field.Value);
                    feedback.Severity = errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                    feedback.Message = errors.Count == 0
                        ? _settings.Localization.GetString("settings.window.saved")
                        : string.Join(Environment.NewLine, errors.Values);
                    feedback.IsOpen = true;
                }
                catch (System.IO.IOException)
                {
                    feedback.Message = _settings.Localization.GetString("settings.window.saveError");
                    feedback.Severity = InfoBarSeverity.Error;
                    feedback.IsOpen = true;
                }
                catch (UnauthorizedAccessException)
                {
                    feedback.Message = _settings.Localization.GetString("settings.window.saveError");
                    feedback.Severity = InfoBarSeverity.Error;
                    feedback.IsOpen = true;
                }
            };
    }

    private static void SetChoices(ComboBox combo, IReadOnlyList<NativeSettingChoice> choices, string selected)
    {
        combo.Items.Clear();
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Title, Tag = choice.Value };
            combo.Items.Add(item);
            if (choice.Value == selected) combo.SelectedItem = item;
        }
    }

    private void SettingsChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            Title = _settings.Localization.GetString("settings.title") + " — Tokens Limits";
            foreach (var localize in _localize) localize();
        }) && !_closed) throw new InvalidOperationException("The settings window dispatcher is unavailable.");
    }
}
