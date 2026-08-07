TelegArm 1.9.0 — portable
=========================

Unzip anywhere and run TelegArm.exe. Nothing is installed and nothing is written outside
the app's own folder and your Documents\TelegArm folder.


REQUIREMENT: Microsoft .NET Framework 4.7 or later
--------------------------------------------------
TelegArm will not start without it, and Windows' error message does not say so.

  ⚠ The portable build CANNOT check this for you. It is compiled against 4.7, so on a PC
    without 4.7 it fails before a single line of TelegArm's own code runs — there is
    nowhere to put the check. (The INSTALLER can check, because it is deliberately built
    against 4.5. If you would rather be told than guess, use the installer.)

Where to get it:

  Windows RT 8.1 / ARM32 (Surface RT, Surface 2)
      https://files.open-rt.party/Software/Redistributables/

      ⚠ Do NOT use Microsoft's web installer on ARM32 — it does not serve an ARM32 build.
        It will download something that cannot run.

  Windows 7 SP1 / 8.1 / 10 / 11 on x86 or x64
      https://dotnet.microsoft.com/download/dotnet-framework

Windows 8, 8.1, 10 and 11 ship a 4.x runtime in the box, but not always 4.7 — check if
TelegArm refuses to start.


WHAT THE PORTABLE BUILD DOES NOT DO
-----------------------------------
Notifications appear as TelegArm's own windows, exactly as in the installed build.

But the portable build creates no Start-Menu shortcut, and Windows identifies a desktop
app to the notification system BY its shortcut. So on a portable copy there is:

  - no Action Center entry (notifications appear and are gone; no history)
  - no Start tile and no tile badge

This is expected, not a fault. With diagnostic logging on you will see exactly one line
saying so:

  [SHELL] Action Center OFF — no Start-Menu shortcut carries AUMID "hamed7ir.TelegArm"

The taskbar icon badge (the unread count on the taskbar button) DOES work portable — it
uses a different, older Windows API that needs no shortcut.

Install with the installer if you want Action Center history and the Start tile.


NOT YET VERIFIED ON ARM32
-------------------------
This release's shell integration was developed and measured on x64 Windows 11. Two things
could not be tested there and are unverified on a Surface RT:

  - whether the Start tile renders avatars (Windows 11 removed live tiles entirely, so the
    dev machine has no tile surface at all)
  - whether the no-focus-steal notification window behaves identically on 8.1

If notifications misbehave on your device, Settings has a "legacy tray balloon" escape
hatch that restores the previous behaviour without a reinstall.
