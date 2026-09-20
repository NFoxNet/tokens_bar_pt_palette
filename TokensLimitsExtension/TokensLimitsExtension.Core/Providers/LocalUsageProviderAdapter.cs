using System.Text.Json;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Loads usage from a local file or the local Ollama endpoint. Transport limits
/// remain owned by the configured provider and are supplied as delegates.
/// </summary>
internal static class LocalUsageProviderAdapter
{
    public static async Task<UsageSnapshot> GetSnapshotAsync(
        UsageProviderDescriptor descriptor,
        IUsageProviderConfiguration configuration,
        HttpClient httpClient,
        Func<string, HttpResponseMessage, UsageProviderRequestException> createHttpFailure,
        Func<HttpContent, CancellationToken, Task<byte[]>> readBoundedResponseBytes,
        CancellationToken cancellationToken)
    {
        var endpoints = UsageProviderEndpointCatalog.For(descriptor.Id);
        var endpoint = endpoints.Count == 0 ? null : endpoints[0];
        if (descriptor.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            && endpoint?.Url is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw createHttpFailure("Ollama API", response);
            }

            var content = await readBoundedResponseBytes(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            return UsageJsonParser.ParseOllama(descriptor, document.RootElement, DateTimeOffset.UtcNow);
        }

        var path = configuration.GetValue(descriptor.Id, "dataPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UsageProviderConfigurationException(
                $"Для локального провайдера {descriptor.DisplayName} укажите путь к файлу данных.");
        }

        if (!File.Exists(path))
        {
            throw new UsageProviderConfigurationException($"Файл данных {path} не найден.");
        }

        var raw = await BoundedLocalFileReader.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var documentFromFile = JsonDocument.Parse(raw);
            return UsageJsonParser.Parse(descriptor, "local", documentFromFile.RootElement, DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return UsageJsonParser.ParseXml(descriptor, "local", raw, DateTimeOffset.UtcNow);
        }
    }
}
