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

## Policy CSP facts (windows-tweak-specialist, checked 2026-11-08)
- Experience CSP: `DisableWindowsConsumerFeatures`, `DisableWindowsSpotlightFeatures` (and every other Spotlight
  policy), `DisableSoftLanding`, `DisableConsumerAccountStateContent`, `DisableCloudOptimizedContent`, `ShowLockOption`
  are Enterprise/Education only (Pro ignores them). `DisableTailoredExperiencesWithDiagnosticData` and
  `DisableThirdPartySuggestions` are user scope and work on Pro.
  https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
- WindowsAI CSP: `TurnOffWindowsCopilot` is deprecated, user scope only, and doesn't cover the new Copilot app.
  `AllowRecallEnablement`=0 (Pro+, 26100.3915+) removes the Recall bits, deletes snapshots, needs a restart. Paint's
  documented policies are only `DisableCocreator`, `DisableImageCreator`, `DisableGenerativeFill`
  (`HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\Paint`, 22621.4870+). `RemoveMicrosoftCopilotApp`,
  `DisableSettingsAgent`, Recall storage/deny-list policies are Enterprise/Education only.
  https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
- MMCSS: `SystemResponsiveness` <10 or >100 is clamped to 20 (100 disables MMCSS); task "GPU Priority" not yet used,
  "SFIO Priority" not used; Priority is treated as 2 when Scheduling Category is High; only threads that call
  `AvSetMmThreadCharacteristics` are affected. `NetworkThrottlingIndex` is not on this page.
  https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- Delivery Optimization policies are documented for Pro/Enterprise/Education (Home not listed).
  https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-deliveryoptimization
- OneDrive policies live under `HKLM\SOFTWARE\Policies\Microsoft\OneDrive` (not `SOFTWARE\Microsoft\OneDrive`).
  `PreventNetworkTrafficPreUserSignIn` keeps its last setting when set back to Not Configured (undo must write 0).
  `KFMBlockOptIn`=1 blocks moving known folders to OneDrive; 2 moves them back. https://learn.microsoft.com/en-us/sharepoint/use-group-policy
- `NoLockScreen` (ADMX_ControlPanelDisplay) is Pro+; the ProEditions note on `gen.no-lock-screen` is correct.
  https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-controlpaneldisplay

## Notes
- Git Bash mangles the non-ASCII project path; netsh/fsutil output is localized - read registry/PowerShell properties.
- Some tweaks are already applied on the owner's PC; live round-trip tests must restore exact originals.
