# TelegArm 2.1.0

Smoother on the Surface RT, and it updates itself now.

## New

- Updates from inside the app: an Update button shows in the title bar when a new version is out. This starts with
  2.1.0, so install this one by hand.
- Animations. You can turn each one off in Settings > Appearance.
- Resizing the window is smoother on the Surface RT, and the menu opens at once.
- Persian and English in one message read the right way.
- The chat list and the lists in dialogs work with the keyboard.
- High contrast support.
- FFmpeg 9: videos decode 2 to 3 times faster on PCs, and AV1 videos play now.
- The installer can make TelegArm start about twice as fast. It takes about 8 minutes on a Surface RT, so it is off
  by default.

## Fixed

- The menu of a group of photos or files acts on the item under the pointer.
- The members list of a group doesn't jump back to the top anymore.

## Good to know

- You need .NET Framework 4.7. On Windows RT 8.1 install it first: https://go.microsoft.com/fwlink/?linkid=2088632
- Your accounts and settings carry over.

## Files

- `TelegArm-2.1.0-Setup.exe`: the installer
- `TelegArm-2.1.0-Portable.zip`: runs without installing
- `SHA256SUMS.txt`: checksums (the updater needs this file in the release)
- `tgcalls-sources-20261016.zip`: source code of the call library (for the GPL)
