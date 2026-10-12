using System.Reflection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;
using TokensLimitsExtension.Settings;

var directory = Path.Combine(Path.GetTempPath(), "TokensLimits.NativeSmoke." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var storage = TokensLimitsSettings.ResolveStorage(directory, null);
    using var settings = (TokensLimitsSettings)Activator.CreateInstance(
        typeof(TokensLimitsSettings), BindingFlags.Instance | BindingFlags.NonPublic, null, [storage], null)!;
    using var host = new NativeSettingsWindowHost(settings);
    var invoke = typeof(NativeSettingsWindowHost).GetMethod("InvokeOnUi", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var appField = typeof(NativeSettingsWindowHost).GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var windowField = typeof(NativeSettingsApplication).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!;
    void OnUi(Action action) => invoke.Invoke(host, [action]);
    NativeSettingsWindow? first = null;
    host.Open();
    if (args.Contains("--startup-only", StringComparer.Ordinal))
    {
        host.Open();
        host.Dispose();
        Console.WriteLine("PASS: package WinUI startup, repeated activation and shutdown.");
        return;
    }
    Thread.Sleep(100);
    OnUi(() =>
    {
        first = (NativeSettingsWindow)windowField.GetValue(appField.GetValue(host))!;
        var panel = (StackPanel)((ScrollViewer)first.Content).Content;
        var groups = panel.Children.OfType<SettingsExpander>().ToArray();
        if (groups.Length != 5) throw new InvalidOperationException("Expected five native groups.");
        foreach (var group in groups) group.IsExpanded = true;
        var providers = groups[1].Items.OfType<SettingsExpander>().ToArray();
        if (providers.Length == 0) throw new InvalidOperationException("No provider expanders.");
        providers[0].IsExpanded = true;
        if (providers[0].HeaderIcon is not BitmapIcon || !providers[0].Items.OfType<SettingsCard>().Any())
            throw new InvalidOperationException("Provider logo/fields missing.");
        Console.WriteLine("PASS: real WinUI window, five groups, provider logo and lazy fields.");
    });
    settings.Settings.Update("{\"tokensLimits.providers.deepseek.enabled\":\"true\"}");
    SettingsCard? newDockRow = null;
    OnUi(() =>
    {
        var panel = (StackPanel)((ScrollViewer)first!.Content).Content;
        var dock = panel.Children.OfType<SettingsExpander>().ElementAt(2);
        dock.IsExpanded = false;
        dock.IsExpanded = true;
        newDockRow = (SettingsCard)dock.Items.Last();
        dock.StartBringIntoView();
    });
    var loaded = false;
    for (var attempt = 0; attempt < 100 && !loaded; attempt++)
    {
        Thread.Sleep(50);
        OnUi(() =>
        {
            var scroll = (ScrollViewer)first!.Content;
            var dock = ((StackPanel)scroll.Content).Children.OfType<SettingsExpander>().ElementAt(2);
            scroll.ChangeView(null, dock.TransformToVisual((StackPanel)scroll.Content).TransformPoint(new Windows.Foundation.Point()).Y, null, true);
            scroll.UpdateLayout();
            loaded = newDockRow!.IsLoaded;
        });
    }
    if (!loaded) throw new InvalidOperationException("New Dock provider field was not rendered.");
    Console.WriteLine("PASS: changed Dock membership renders in the open window.");
    OnUi(() => first!.AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 480)));
    Thread.Sleep(100);
    OnUi(() =>
    {
        var scroll = (ScrollViewer)first!.Content;
        scroll.UpdateLayout();
        var panel = (StackPanel)scroll.Content;
        foreach (var group in panel.Children.OfType<SettingsExpander>())
        foreach (var row in group.Items.OfType<SettingsCard>().Concat(group.Items.OfType<SettingsExpander>().SelectMany(provider => provider.Items.OfType<SettingsCard>())))
        {
            var control = (Control)row.Content;
            var bounds = control.TransformToVisual(scroll).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            if (bounds.Right > scroll.ActualWidth + 1) throw new InvalidOperationException($"Input clipped: {row.Header}, right={bounds.Right}, viewport={scroll.ActualWidth}, width={control.ActualWidth}.");
        }
        Console.WriteLine("PASS: section input bounds at 640x480.");
    });
    host.Open();
    OnUi(() =>
    {
        if (!ReferenceEquals(first, windowField.GetValue(appField.GetValue(host)))) throw new InvalidOperationException("Duplicate window.");
        first!.Close();
    });
    host.Open();
    OnUi(() =>
    {
        if (ReferenceEquals(first, windowField.GetValue(appField.GetValue(host)))) throw new InvalidOperationException("Closed window reused.");
        Console.WriteLine("PASS: repeat activation, close and reopen.");
    });
}
finally
{
    Directory.Delete(directory, true);
}
Console.WriteLine("PASS: shutdown and isolated settings cleanup.");