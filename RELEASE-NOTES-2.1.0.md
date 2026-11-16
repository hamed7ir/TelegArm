# TelegArm 2.1.0

A polish update. TelegArm is smoother on the Surface RT, updates itself from GitHub, and Persian and English read right
in the same message.

## New

- Updates from inside the app. When a new version is out, an Update button shows in the title bar. One click
  downloads and installs it. (This works from 2.1.0 on, so install this one by hand.)
- Animations: switches slide, rows and buttons fade on hover, the menu slides in, popups fade in, scrolling glides,
  and pages slide in a narrow window. Each one has its own switch in Settings > Appearance.
- Resizing the window is much smoother on the Surface RT, and long chats no longer freeze when you let go.
- The menu opens at once, and TelegArm starts faster.
- A slimmer title bar.
- Persian and English in one message: each line reads in its own direction, and English words keep their order
  inside a Persian line.
- Keyboard: in the search box, Up, Down and Enter move through the chat list and open a chat. Lists in dialogs work
  with Tab, the arrows and Space. Ctrl+PageUp and Ctrl+PageDown switch chats.
- High contrast: TelegArm follows Windows' high contrast colors.
- The installer can prepare TelegArm for your device, so it starts about twice as fast. It takes a while (about 8
  minutes on a Surface RT) and is off by default. "Install updates fast" in Settings > Advanced skips it on updates.
- A group of files shows as a list of files.
- FFmpeg 9. Videos decode 2 to 3 times faster on PCs, and AV1 videos play now. On the Surface RT the speed is the same
  as before.

## Fixed

- The menu of a group of photos or files acts on the item under the pointer.
- The members list of a group no longer jumps back to the top when more members load.
- Simpler wording all over the app.

## Good to know

- You need .NET Framework 4.7. On Windows RT 8.1 install it first: https://go.microsoft.com/fwlink/?linkid=2088632
- Your accounts and settings from 2.0 carry over.

## Files

- `TelegArm-2.1.0-Setup.exe`: the installer
- `TelegArm-2.1.0-Portable.zip`: runs without installing
- `SHA256SUMS.txt`: checksums
- `tgcalls-sources-20261016.zip`: source code of the call library (for the GPL)
