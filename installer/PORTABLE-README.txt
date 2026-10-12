TelegArm {VERSION} — portable
=========================

Unzip anywhere and run TelegArm.exe. Nothing is installed. TelegArm keeps your accounts,
settings and cache in your user profile (AppData\Local\TelegArm, or Documents\TelegArm on
Windows RT), never in its own folder — so a portable copy and an installed copy on the same
PC share them.


REQUIREMENT: Microsoft .NET Framework 4.7 or later
--------------------------------------------------
TelegArm will not start without it, and Windows' error message does not say so.

  ⚠ The portable build CANNOT check this for you. It is compiled against 4.7, so on a PC
    without it, it fails before a single line of TelegArm's own code runs. (The INSTALLER
    can check, because it is deliberately built against .NET 4.0: press "Check
    requirements" in it.)

Where to get it:

  Windows RT 8.1 (Surface RT, Surface 2) — RT ships .NET 4.5, so install 4.7 first:
      https://go.microsoft.com/fwlink/?linkid=2088632

  Windows 10 ARM32 — already has it. No action needed.

  Windows 7 SP1 / 8.1 / 10 / 11 on x86, x64 or ARM64
      https://dotnet.microsoft.com/download/dotnet-framework

Windows 8.1, 10 and 11 ship a 4.x runtime in the box, but not always 4.7 — check if
TelegArm refuses to start.


WHAT THE PORTABLE BUILD DOES NOT DO
-----------------------------------
Notifications appear as TelegArm's own windows, exactly as in the installed build.

But the portable build creates no Start-Menu shortcut, and Windows identifies a desktop
app to the notification system BY its shortcut. So a portable copy gets no Action Center
entries (notifications appear and are gone; no history). This is expected, not a fault.
With diagnostic logging on you will see exactly one line saying so:

  [SHELL] Action Center OFF — no Start-Menu shortcut carries AUMID "hamed7ir.TelegArm"

The taskbar icon badge (the unread count on the taskbar button) DOES work portable — it
uses a different, older Windows API that needs no shortcut.

Install with the installer if you want Action Center history.

If notifications misbehave on your device, Settings has a "legacy tray balloon" escape
hatch that restores the previous behaviour without a reinstall.
