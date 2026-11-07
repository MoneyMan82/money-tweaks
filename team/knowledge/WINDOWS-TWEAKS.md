# Windows tweak knowledge

Owned by windows-tweak-specialist. Add durable facts with a source link and date.

## Rules for every tweak
- Values from Microsoft Learn (Policy CSP, ADMX/Group Policy reference, Win32 docs) or the vendor's own docs. Tweak
  blogs and forum posts are leads, not evidence.
- Record MinBuild (Windows 11 = build 22000+) and Editions (many `Policies` keys are ignored on Home).
- Prefer HKCU per-user settings over HKLM when both work (no admin, easier undo).
- Undo must restore "absent" correctly; use RemoveKeyIfCreated when the tweak creates a policy key.
- Policies under `HKLM\SOFTWARE\Policies` show "Some settings are managed by your organization" in Settings - mention
  it in the description when relevant.
- Feature updates can reset settings (I-006 Tweak Guardian idea addresses detecting that).

## Never (security weakening - also tested by TweakSourcesTests)
UAC/EnableLUA, ConsentPromptBehaviorAdmin, Defender, SmartScreen, Mark-of-the-Web (SaveZoneInformation),
Spectre/Meltdown mitigations (FeatureSettingsOverride), automatic updates off (NoAutoUpdate), BitLocker, firewall,
LSA/credential protection, typo-squatting checker.

## Already rejected (see docs/TWEAK-SOURCES.md "Reviewed but not added")
Script removal of Edge/OneDrive/Widgets, store.db ACL hack, downloaded hosts lists, F1 TypeLib override, Game Bar
protocol hijack, deleting system keys for duplicate drives, Start layout feature flags, UTC hardware clock, full IPv6
disable, Explorer bag reset, mouse/keyboard queue sizes and "network registry boosts" (placebo).

## Notes
- Git Bash mangles the non-ASCII project path; netsh/fsutil output is localized - read registry/PowerShell properties.
- Some tweaks are already applied on the owner's PC; live round-trip tests must restore exact originals.
