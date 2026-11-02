## Summary

<!-- What does this change do? Link the issue or motivation. -->

## Checklist

- [ ] `dotnet build CSharp_XISOSharp.sln -c Release -m:1` is clean (0 warnings)
- [ ] `dotnet test tests/XISOSharp.Tests/XISOSharp.Tests.csproj -c Release` passes
- [ ] `dotnet format whitespace CSharp_XISOSharp.sln --verify-no-changes --no-restore` passes
- [ ] Existing byte-parity behavior is unchanged (or the change is documented)
- [ ] Public API changes are additive and XML-documented
- [ ] `docs/`, `README.md`, `WhatsNew.md` and release notes updated when needed
- [ ] No checked-in game data or GPL-derived code was added

## Testing

<!-- How was this verified? Reference-tool diff, new tests, manual commands. -->
