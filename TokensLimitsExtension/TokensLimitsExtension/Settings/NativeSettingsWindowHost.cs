using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TokensLimitsExtension.Settings;

internal sealed class NativeSettingsWindowHost : IDisposable
{
    private readonly TokensLimitsSettings _settings;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _uiThread;
    private DispatcherQueue? _dispatcher;
    private NativeSettingsApplication? _application;
    private Exception? _threadFailure;
    private bool _disposed;

    public NativeSettingsWindowHost(TokensLimitsSettings settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_uiThread is null)
            {
                _uiThread = new Thread(RunApplication)
                {
                    IsBackground = true,
                    Name = "Tokens Limits settings UI",
                };
                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.Start();
            }

            _started.Task.GetAwaiter().GetResult();
            ThrowIfThreadStopped();
            InvokeOnUi(() => _application!.OpenSettingsWindow());
        }
    }

    public void Dispose()
    {
        Thread? uiThread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            uiThread = _uiThread;
            if (uiThread is null)
            {
                return;
            }

            try
            {
                _started.Task.GetAwaiter().GetResult();
                if (uiThread.IsAlive)
                {
                    InvokeOnUi(() => _application!.Shutdown());
                }
            }
            finally
            {
                if (!ReferenceEquals(Thread.CurrentThread, uiThread))
                {
                    uiThread.Join();
                }
            }
        }

        if (_threadFailure is { } failure)
        {
            throw new InvalidOperationException("The settings UI thread failed.", failure);
        }
    }

    private void RunApplication()
    {
        try
        {
            Application.Start(_ =>
            {
                _dispatcher = DispatcherQueue.GetForCurrentThread()
                    ?? throw new InvalidOperationException("The WinUI dispatcher queue was not created.");
                _application = new NativeSettingsApplication(_settings);
                _application.OpenSettingsWindow();
                _started.TrySetResult();
            });
        }
        catch (Exception exception)
        {
            _threadFailure = exception;
            _started.TrySetException(exception);
        }
    }

    private void InvokeOnUi(Action action)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable.");
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completed.TrySetResult();
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
        }))
        {
            ThrowIfThreadStopped();
            throw new InvalidOperationException("The WinUI dispatcher is shutting down.");
        }

        completed.Task.GetAwaiter().GetResult();
    }

    private void ThrowIfThreadStopped()
    {
        if (_threadFailure is { } failure)
        {
            throw new InvalidOperationException("The settings UI thread failed.", failure);
        }

        if (_uiThread is { IsAlive: false })
        {
            throw new InvalidOperationException("The settings UI thread has stopped.");
        }
    }
}