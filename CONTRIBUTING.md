# Contributing

Thanks for improving Tokens Limits.

1. Open an issue first for a new provider or a behavioural change.
2. Create a focused branch and keep secrets, API responses and build output out of commits.
3. Add or update unit/integration tests for changed behaviour.
4. Run from `TokensLimitsExtension/`:

   ```powershell
   dotnet restore .\TokensLimitsExtension.sln
   dotnet build .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
   dotnet test .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
   ```

5. Update `doc/` whenever the user-visible behaviour, configuration, provider contract or packaging changes.

Please keep provider metrics truthful: do not infer a percentage when the upstream API only returns a balance, cost or count.
