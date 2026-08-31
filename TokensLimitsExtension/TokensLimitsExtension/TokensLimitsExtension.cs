// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace TokensLimitsExtension;

[Guid("d76e2329-7747-4ea9-893f-d0e907245b20")]
public sealed partial class TokensLimitsExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly TokensLimitsExtensionCommandsProvider _provider = new();
    private int _disposed;

    public TokensLimitsExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent ?? throw new ArgumentNullException(nameof(extensionDisposedEvent));
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _provider.Dispose();
        }
        finally
        {
            _extensionDisposedEvent.Set();
            GC.SuppressFinalize(this);
        }
    }
}
