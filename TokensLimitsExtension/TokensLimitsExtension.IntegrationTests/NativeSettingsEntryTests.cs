using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class NativeSettingsEntryTests
{
    [Fact]
    public void OnlyExplicitOpenActionLaunchesWindowAndNeverSavesInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            var opened = 0;
            using var commandSettings = new GroupedCommandSettings(settings, () => opened++);
            var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(commandSettings.SettingsPage.GetContent()));
            Assert.Equal(0, opened);
            var before = File.ReadAllText(settings.FilePath);
            form.SubmitForm("{\"tokensLimits.refreshInterval\":\"120\"}", "{\"openSettings\":true}");
            Assert.Equal(1, opened);
            Assert.Equal(before, File.ReadAllText(settings.FilePath));
            form.SubmitForm("{}", "{\"openSettings\":true}");
            Assert.Equal(2, opened);
            Assert.Throws<ArgumentException>(() => form.SubmitForm("{}", "{}"));
            Assert.Equal(2, opened);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SettingsEntryStillExposesCommandPaletteComInterfaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            using var entry = new GroupedCommandSettings(settings, () => { });
            var pointer = WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.ICommandSettings>.FromManaged(entry);
            try
            {
                var projected = WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.ICommandSettings>.FromAbi(pointer);
                Assert.NotNull(projected.SettingsPage);
            }
            finally { WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.ICommandSettings>.DisposeAbi(pointer); }
            var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(entry.SettingsPage.GetContent()));
            var formPointer = WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.IFormContent>.FromManaged(form);
            try
            {
                var projected = WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.IFormContent>.FromAbi(formPointer);
                Assert.Single(JsonNode.Parse(projected.TemplateJson)!["actions"]!.AsArray());
            }
            finally { WinRT.MarshalInterface<Microsoft.CommandPalette.Extensions.IFormContent>.DisposeAbi(formPointer); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void SettingsPageContainsOneOpenButtonAndNoEditableOrSecretFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(settings.CommandSettings.SettingsPage.GetContent()));
            var card = JsonNode.Parse(form.TemplateJson)!;
            var action = Assert.Single(card["actions"]!.AsArray())!;
            Assert.Equal("Action.Submit", action["type"]!.GetValue<string>());
            Assert.Equal("none", action["associatedInputs"]!.GetValue<string>());
            Assert.DoesNotContain("Input.", form.TemplateJson);
            Assert.DoesNotContain("tokensLimits.providers.", form.TemplateJson);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
