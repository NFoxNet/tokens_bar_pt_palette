# Tokens Limits

Tokens Limits is a Microsoft PowerToys Command Palette extension that shows usage limits and account metrics for Codex and other AI providers in one Dock band.

## Install

Download the latest release from [GitHub Releases](https://github.com/NFoxNet/tokens_bar_pt_palette/releases/latest), keep the MSIX, certificate, installer and checksum file together, then run `Install-TokensLimitsExtension.cmd`. Reload Command Palette extensions in PowerToys after installation.

The installer verifies the package signature and checksums. Updates retain settings and encrypted provider keys.

## Highlights

- Codex usage limits with a clearly marked local fallback when the API is unavailable.
- DeepSeek balance and usage metrics, plus the project’s other provider adapters.
- One shared refresh pipeline and snapshot for Dock, overview and detail pages.
- English by default, Russian included; additional languages can be added as JSON files in `TokensLimitsExtension/lang/`.
- Provider-specific API keys and settings stored securely in the Windows user profile.

## Documentation

- [Русская версия README](README.ru.md)
- [Project documentation](TokensLimitsExtension/doc/README.md)
- [Development and testing](TokensLimitsExtension/doc/development.md)
- [Release and installation details](TokensLimitsExtension/doc/release.md)

## License

[MIT](LICENSE)
