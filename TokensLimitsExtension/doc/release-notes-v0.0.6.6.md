# TokensLimitsExtension v0.0.6.6

## Dock stability

Changing between individual provider buttons and the summary now updates the items inside one persistent Dock band. The band keeps its command ID and page instance, so Command Palette can retain the pin and refresh its contents. The summary uses a separate nested command and the existing usage cache.

## Provider icons

Added icon mappings for OpenAI, Azure OpenAI, Moonshot, Ollama, Augment, ClinePass and Vertex AI. Added a contrasting background so light and dark provider marks remain legible across Command Palette themes. Both architectures include icon resources for all 69 providers.

## OpenAI usage and billing

Provider setup now identifies the organization Admin API key as required for OpenAI usage and spend. The displayed spend is the total for the selected reporting period. The OpenAI page also links directly to the organization's billing overview.

The optional legacy credit lookup displays a balance only when OpenAI returns a numeric available amount. If the billing endpoint rejects the request, usage and spend remain available. OpenAI's public Admin API does not expose the current prepaid credit balance. For the actual balance, use the [OpenAI billing overview](https://platform.openai.com/account/billing).

## Installation

Requires Windows 11 build 22000 or newer, PowerToys Command Palette and [Windows App Runtime 1.8](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Download the MSIX for your architecture, the public certificate and both installer files into one folder. Run `Install-TokensLimitsExtension.cmd` as your normal user. Install over the existing package to retain settings and protected credentials. Distribution file checksums are in `SHA256SUMS.txt`.
