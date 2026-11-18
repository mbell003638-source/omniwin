# OmniWin app

A desktop app for OmniWin: dictation with local speech models, a command box that sends your words to OmniWin
(`omniwin_do`), a voice command that fills that box, and "polish selected text". It starts its own
`OmniWin.Server.exe` and may call only nine OmniWin tools; OmniWin never clicks or types in this app's windows.

It is built from a copy of [Handy](https://github.com/cjpais/Handy) (MIT, copyright CJ Pais) at commit
`aca8985190c3ad7f361d8a80cb30f179513ed690`, renamed, with its updater removed. `LICENSE` is Handy's licence;
`NOTICE.md` lists every change. Handy's name, logo, mascot and sponsor images are not used.

Every hotkey starts with no key. Build: see `BUILD.md` (Rust, CMake, MSVC Build Tools, the Vulkan SDK and Bun) and
`build-omniwin-app.ps1`. A prebuilt copy is in the GitHub Release zip under `publish/app`.
