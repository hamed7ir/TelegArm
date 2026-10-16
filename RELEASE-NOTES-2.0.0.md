# TelegArm 2.0.0

Big update. TelegArm has calls now, a new look, and it works much better on the Surface RT.

## New

- Voice calls, video calls and group voice chats. They work on the Surface RT too, camera included.
- New look. TelegArm draws its own windows now, MaterialSkin is gone.
- Pick any accent color, theme colors and chat backgrounds. You can also turn on a wallpaper background for the window.
- Typing on the Surface RT works now. The message box shows color emoji and grows up to 13 lines.
- Sharp at any display scale. You can change the size live with Ctrl+= and Ctrl+-.
- One column on small screens, like Telegram Desktop.
- Videos and music play with FFmpeg now instead of VLC. The download is about 85 MB smaller.
- Round videos on Windows 10 have a live camera preview.
- New proxy options: SOCKS5 and a built-in WebSocket tunnel.
- More Telegram features: scheduled messages, all reactions, folder editing, quizzes, translation, privacy exceptions, auto-delete and more.
- Keyboard shortcuts, and the window remembers its size and place.
- Persian and Arabic text goes right to left based on its first letter.

## Fixed

- Settings are saved safely now, a crash can't break them anymore.
- Downloads can be paused, resumed and canceled properly.
- Persian and emoji text doesn't turn bold randomly anymore.
- Lots of small fixes from testing on the Surface RT.

## Good to know

- You need .NET Framework 4.7. On Windows RT 8.1 install it first: https://go.microsoft.com/fwlink/?linkid=2088632
- Calls need Windows 8.1 or newer.
- Your accounts and settings from 1.9 carry over.

## Files

- `TelegArm-2.0.0-Setup.exe`: the installer
- `TelegArm-2.0.0-Portable.zip`: runs without installing
- `SHA256SUMS.txt`: checksums
- `tgcalls-sources-20261016.zip`: source code of the call library (for the GPL)
