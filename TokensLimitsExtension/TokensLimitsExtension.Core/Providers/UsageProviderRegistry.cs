namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Runtime registry of instantiated usage providers, preserving registration order.
/// </summary>
public sealed class UsageProviderRegistry
{
    private readonly Lock _gate = new();
    private readonly List<IUsageProvider> _providers = [];

    public UsageProviderRegistry(IEnumerable<IUsageProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (var provider in providers)
        {
            Register(provider);
        }
    }

    public IReadOnlyList<IUsageProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers.ToArray();
            }
        }
    }

    public void Register(IUsageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(provider.Descriptor);

        lock (_gate)
        {
            if (_providers.Any(existing => string.Equals(
                    existing.Descriptor.Id,
                    provider.Descriptor.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"A usage provider with ID '{provider.Descriptor.Id}' is already registered.",
                    nameof(provider));
            }

            _providers.Add(provider);
        }
    }

    public IUsageProvider? Find(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        lock (_gate)
        {
            return _providers.FirstOrDefault(provider => string.Equals(
                provider.Descriptor.Id,
                providerId,
                StringComparison.OrdinalIgnoreCase));
        }
    }
}
