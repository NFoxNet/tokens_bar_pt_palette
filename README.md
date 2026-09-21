# Tokens Limits

Tokens Limits is a Microsoft PowerToys Command Palette extension that shows usage limits and account metrics for Codex and other AI providers in one Dock band.

## Install

Download the MSIX, certificate, installer and checksum file from [GitHub Releases](https://github.com/NFoxNet/tokens_bar_pt_palette/releases/latest), keep them together, then double-click `Install-TokensLimitsExtension.cmd`. The installer closes PowerToys before the update, migrates a stale Tokens Limits Dock pin when Command Palette is stopped, and starts PowerToys again. It requests administrator approval only for certificate and package installation; do not use **Run as administrator**, so PowerToys restarts with your normal user permissions. If PowerToys was not running before installation, start it when you are ready to use the extension.

For release details and field checks for v0.0.5.6, see the [release page](https://github.com/NFoxNet/tokens_bar_pt_palette/releases/tag/v0.0.5.6) and [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.6.md).

Windows validates the package signature during installation; compare the downloaded files with `SHA256SUMS.txt` before installing. In-place updates retain settings and encrypted provider keys.

## Highlights

- Codex usage limits with a clearly marked local fallback when the API is unavailable.
- DeepSeek balance and usage metrics, plus the project’s other provider adapters.
- One shared refresh pipeline and snapshot for Dock, overview and detail pages.
- English by default, Russian included; additional languages can be added as JSON files in `TokensLimitsExtension/lang/`.
- Provider-specific API keys and settings stored securely in the Windows user profile.

## Supported providers

The extension includes adapters for the providers below. The available metrics depend on each provider's API, subscription and configured authentication.

- **Codex:** Codex.
- **A–C:** Abacus AI, ai&, Alibaba Coding Plan, Alibaba Token Plan, Amp, Antigravity, Augment, Azure OpenAI, AWS Bedrock, Chutes, Claude, ClawRouter, ClinePass, Codebuff, Command Code, Copilot, Crof and Cursor.
- **D–G:** Deepgram, DeepInfra, DeepSeek, Devin, Doubao, Droid, ElevenLabs, Fireworks, Gemini, Grok and Groq.
- **I–M:** IBM Bob, JetBrains AI, Kilo, Kimi Code, Kiro, LiteLLM, LLM Proxy, LongCat, Manus, Xiaomi MiMo, MiniMax, Mistral and Moonshot.
- **N–R:** Neuralwatt, Notion AI, Ollama, OpenAI, OpenCode, OpenCode Go, OpenRouter, Perplexity, Poe, Qoder and Qwen Cloud.
- **S–Z:** Sakana AI, StepFun, sub2api, Synthetic, T3 Chat, Venice, Vertex AI, Warp, Wayfinder, Windsurf, xAI, z.ai, Zed, ZenMux and ZoomMate.

https://github.com/user-attachments/assets/a5dc3be1-7459-4a65-81e8-e5112dd34086

<img width="3070" height="164" alt="193259" src="https://github.com/user-attachments/assets/aead3072-0ab0-4614-8fd7-d8b3baa9e298" />

## Documentation

- [Русская версия README](README.ru.md)
- [Project documentation](TokensLimitsExtension/doc/README.md)
- [Development and testing](TokensLimitsExtension/doc/development.md)
- [Release and installation details](TokensLimitsExtension/doc/release.md)

## License

[MIT](LICENSE)
