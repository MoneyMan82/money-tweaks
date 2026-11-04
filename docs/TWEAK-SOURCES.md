# Where tweak ideas come from

Money Tweaks' tweaks are its own implementation: every change is a Money Tweaks operation (registry value, service
start type, scheduled task, power setting or command) with backup, read-back verification and undo, and every
description is written for Money Tweaks. No code, documentation text, configuration files or assets from other projects
are included or run. Windows registry locations and policy values are facts about Windows, documented by Microsoft or by
the vendor of the browser or driver they configure.

Since 1.79, a tweak whose idea came from reviewing another project shows that project in its details ("Source"), and is
listed in `src/MoneyTweaks.Core/Catalog/TweakCatalog.Expanded.cs`.

| Source | License | How it was used | Tweaks |
|---|---|---|---:|
| [Win11Debloat](https://github.com/Raphire/Win11Debloat) by Raphire | MIT | Its `Regfiles` list was reviewed for settings Money Tweaks didn't have; values checked against Microsoft documentation. | 20 |
| [WinUtil](https://github.com/ChrisTitusTech/winutil) by Chris Titus Tech | MIT | `config/tweaks.json` was reviewed for settings Money Tweaks didn't have. | 15 |
| [O&O ShutUp10++](https://www.oo-software.com/en/shutup10) | Freeware, closed source | Only the names in its public setting list were used as a checklist; each setting was implemented from Microsoft's own policy documentation. | 14 |
| [Sophia Script for Windows](https://github.com/farag2/Sophia-Script-for-Windows) | MIT | Its function list (`Sophia.ps1`) was reviewed for settings Money Tweaks didn't have. | 13 |
| [Optimizer](https://github.com/hellzerg/optimizer) by hellzerg | GPL-3.0 | Feature list only, as ideas. No code was looked at or used. | 5 |

Microsoft references used to check values: the
[Policy CSP](https://learn.microsoft.com/windows/client-management/mdm/policy-configuration-service-provider) and Group
Policy documentation, plus the enterprise policy documentation of
[Microsoft Edge](https://learn.microsoft.com/deployedge/microsoft-edge-policies),
[Google Chrome](https://chromeenterprise.google/policies/),
[Mozilla Firefox](https://mozilla.github.io/policy-templates/) and
[Brave](https://support.brave.com/hc/en-us/articles/360039248271-Group-Policy).

## Reviewed but not added

| Idea (seen in) | Why not |
|---|---|
| Disable UAC / Admin Approval Mode (Sophia) | Weakens Windows security. |
| Disable Mark-of-the-Web / SaveZoneInformation (Sophia) | Weakens protection against downloaded malware. |
| Disable Defender, SmartScreen, PUA detection, sample submission, LSA protection, Edge typo-squatting checker (Optimizer, O&O, Sophia) | Weakens Windows security. |
| Disable Spectre/Meltdown mitigations | Weakens Windows security for a tiny gain. |
| Turn off automatic Windows Updates or Store app updates (O&O, Optimizer) | Leaves security fixes uninstalled. |
| Disable BitLocker / device encryption (WinUtil, Win11Debloat) | Removes data protection; not a performance setting. |
| Remove Edge, OneDrive, Widgets by script (WinUtil) | Runs uninstallers that can't be reliably undone; OneDrive removal already exists as an undoable App Removal tweak. |
| Block Store search results by changing file permissions on store.db (WinUtil) | Hacky ACL change on an app's database; not cleanly reversible. |
| Hosts-file block lists downloaded from the internet (WinUtil) | Downloads and runs third-party data. |
| F1 key doesn't open Bing help (Sophia) | Works by overriding a system COM type library per user; removing the override cleanly isn't guaranteed. |
| Game Bar protocol hijack (Win11Debloat) | Rewrites the ms-gamebar protocol handler; fragile and can break Xbox features. |
| Hide duplicate removable drives (Win11Debloat) | Deletes a system registry key; Money Tweaks only adds reversible changes. |
| Revert the Windows 11 Start layout via feature overrides (WinUtil) | Undocumented feature-flag override that changes between builds. |
| Hardware clock in UTC (WinUtil, Optimizer) | Only for dual-boot with Linux; wrong time otherwise. |
| Disable IPv6 completely (WinUtil) | Breaks some networks and Windows features; "Prefer IPv4" already exists. |
| Explorer folder "bag" reset (WinUtil) | Deletes saved folder views (not reversible); the reversible part was added as "Stop File Explorer guessing folder types". |
| Mouse/keyboard data queue sizes, "network registry boosts", large icon caches | Placebo or undocumented; no measurable effect. |
