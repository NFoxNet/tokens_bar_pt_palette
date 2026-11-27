using System;
using Microsoft.UI.Xaml;

namespace TokensLimitsExtension.Settings;

public sealed partial class NativeSettingsApplication : Application
{
    private readonly TokensLimitsSettings _settings;
    private NativeSettingsWindow? _window;

    internal NativeSettingsApplication(TokensLimitsSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    public void OpenSettingsWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var window = new NativeSettingsWindow(_settings);
        window.Closed += OnWindowClosed;
        _window = window;
        window.Activate();
    }

    public void Shutdown()
    {
        _window?.Close();
        _window = null;
        Exit();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }
    }
}