using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TokensLimitsExtension;

/// <summary>
/// Resolves provider IDs to offline package assets. The allow-list makes a
/// missing or newly added provider use the neutral icon instead of a broken URI.
/// </summary>
internal static class ProviderIconCatalog
{
    private static readonly HashSet<string> CodexBarIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "abacus", "aiand", "alibaba", "amp", "antigravity", "bedrock", "chutes",
        "claude", "clawrouter", "codebuff", "codex", "commandcode", "copilot", "crof",
        "cursor", "deepgram", "deepinfra", "deepseek", "devin", "doubao", "elevenlabs",
        "factory", "fireworks", "gemini", "grok", "groq", "ibmbob", "jetbrains", "kilo",
        "kimi", "kiro", "litellm", "llmproxy", "longcat", "manus", "mimo", "minimax",
        "mistral", "neuralwatt", "notion", "opencode", "opencodego", "openrouter",
        "perplexity", "poe", "qoder", "qwencloud", "sakana", "stepfun", "sub2api",
        "synthetic", "t3chat", "venice", "warp", "wayfinder", "windsurf", "xai", "zai",
        "zed", "zenmux", "zoommate",
    };

    public static IconInfo For(string providerId)
    {
        var assetId = providerId.Equals("alibabatokenplan", StringComparison.OrdinalIgnoreCase)
            ? "alibaba"
            : providerId;
        var path = CodexBarIcons.Contains(assetId)
            ? $"Assets\\Providers\\ProviderIcon-{assetId}.64.png"
            : "Assets\\Providers\\ProviderIcon-generic.64.png";
        return IconHelpers.FromRelativePath(path);
    }
}
