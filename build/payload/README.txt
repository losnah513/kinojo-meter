KINOJO Meter

- Installed release metadata is stored in version.json and must match the EXE file version.

- The user only enters a six-character PASS KEY and selects one owned character.
- The app then moves to the system tray and handles capture, retries, combat detection, and overlay display automatically.
- Npcap is attempted first. WinDivert is used as the fallback capture engine.
- Capture startup failures retry automatically after 5, 15, 30, and 60 seconds.
- Detailed capture errors are written to local diagnostics and are visible only in the administrator tray menu.
- The normal overlay is a lightweight DPS view. It does not expose capture restart, raw HRESULT messages, logout, or server controls.
- The app requests administrator elevation at startup so WinDivert can load without requiring a manual right-click action.
- The installer defaults to C:\Program Files\KINOJO Meter and allows the install path to be changed.
- One full installer handles new install, update, and same-version repair automatically.
- Update and repair use staging, backup, file verification, launch verification, and automatic rollback.
- Existing install path and shortcut choices are preserved during update.
- User preferences and diagnostics remain under %LOCALAPPDATA%\KINOJO Meter and are not removed by program updates.
- The installer writes install-manifest.json with managed file sizes and SHA-256 values.
- The installer registers Start Menu, optional desktop shortcut, repair, and uninstall entries.
- The client can consume an optional Server Engine desktopUpdate manifest and verify the installer SHA-256 before applying an update.
- The AION2 binary decoder remains unvalidated. Production encounter upload stays blocked until fixture validation is complete.
- PASS KEY, session tokens, Supabase service role keys, and database passwords are not written to diagnostics.
