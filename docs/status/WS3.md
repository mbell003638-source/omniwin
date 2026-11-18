# WS3 system tools and OCR status

Updated 2026-11-08 (work order `15_Full_Project_Completion_Handoff.md`, Phase 1 item 3). Contract 2.3 plus the notes at the end ("Contract notes for 2.4").

## Status

| Item | State |
|---|---|
| `OmniWin.SystemTools`: apps, launch, windows, audio, media, system info, clipboard | Done, unit-tested |
| `OmniWin.Ocr`: capture, Windows OCR, `oN` ids, `IOcrRefResolver` | Done, unit-tested; real screen capture not run (see "Not verified here") |
| Nine MCP tools registered | Done (`Tools/SystemTools.cs`, `Tools/OcrTools.cs`, lines in `Program.cs`); read back from a running server over stdio |
| `omniwin_status` components `system_tools` and `ocr` | Done (`IComponentStatus`, state `ready`) |
| Live-session tests (real volume, clipboard, windows, launch, OCR of a fixture, `oN` click) | Not run: tagged and skipped until Phase 5 (list below) |

## Tools (contract §8, names and parameters exactly as listed)

| Tool | Params | Notes |
|---|---|---|
| `apps_list` | `filter?` | Start-menu apps from the shell's AppsFolder; cached 60 s; max 300 listed |
| `app_launch` | `app`, `args?`, `expect?` | Start-menu name or AUMID only; see limits |
| `window_manage` | `window`, `action`, `x?`, `y?`, `width?`, `height?` | focus, minimize, maximize, restore, move, resize, snap_left, snap_right, close; `close` is labelled Risky |
| `audio` | `volume?`, `mute?` | No params reads; otherwise sets and reads back |
| `media` | `action` | play_pause, next, previous, stop (Windows media sessions; no key presses) |
| `system_info` | `kind` | network (no addresses), battery, displays, time |
| `clipboard_get` | none | Risky label; text under the untrusted header; never logged |
| `clipboard_set` | `text` | Only the length reaches the gate or any log |
| `screen_ocr` | `window?`, `region?` | Text only; `oN` ids clickable through `desktop_act` (click, double_click, right_click) |

## Design limits kept (listed for the owner)

1. `app_launch` never passes `args` to a command processor or script host (`cmd`, `powershell`, `pwsh`, `powershell_ise`, `wt`, `WindowsTerminal`, `wscript`, `cscript`, `mshta`): a command line there would bypass the no-delete check that typed commands go through. Terminals can be launched, without args.
2. `app_launch` refuses an arbitrary path, URL, file or command line unless the exact path is in `app_launch_allow_paths`. (Both are pre-existing contract rules, not new blocks.)
3. Start-menu apps take no `args` at all (the shell launch cannot carry them). `args` work only for an allow-listed executable. This is a capability limit, not a policy block.
4. The safety checks stay: stop key, user-activity pause (only for `app_launch` and `window_manage`, which send real input or change the foreground), process blocklist (secure desktop, sign-in prompts, `process_blocklist_extra`), and OmniWin's own windows.

## How the open questions were decided (work order §4)

| Q | Decision in the code |
|---|---|
| Q1 window argument | Same forms as `desktop_observe`: `wN` id, process name (with or without `.exe`), or a unique title substring, resolved through `IDesktopEngine.ListWindowsAsync`. No match or several matches: `blocked not_found`, with the candidate ids in the detail (no new reason code). |
| Q2 foreground result | `BringToForegroundAsync` still returns only `bool`. Elevated, secure-desktop and blocklisted windows are refused before it is called. On `false` the result is `error foreground_failed` with `input_sent=true`, because a focus attempt may already have sent input. No Desktop edit. |
| Q3 launch of shells | Terminals may be launched (owner policy); `args` never reach a command processor. The resolved process goes in `ActionDescriptor.Process`, so the gate checks it against the blocklist. |
| Q4 default checks | volume and mute read back; window rect and state read back (±2 px); `close`: window gone, else `unverified`; clipboard text read back; `app_launch`: the caller's `expect`, else a new window within 2 s (`confirmed`) or `unverified`; `media`: `confirmed` only when the playback state changes, skipping is `unverified`. |
| Q5 non-act status | The §7 interfaces are unchanged. Each service has richer `...OutcomeAsync` methods (`SystemOutcome<T>`, `OcrOutcome`) that carry the denial, block or timeout; the tool files use them. The plain §7 methods throw `SystemToolFault` / `OcrFault` with that result. |
| Q6 OCR scope | Forbidden windows (OmniWin itself, secure desktop, blocklisted) are denied when named and painted black inside a region capture. Each word's window is the top-level window under its box at capture time. Default is the foreground window. |
| Q7 text and redaction | One-line formats in `SystemFormat` and `OcrFormat` (tested). Untrusted header on `apps_list`, network info, `clipboard_get` text and `screen_ocr`. `clipboard_set` text is never previewed or logged: only `<N chars>`. |
| Q8 run log | System acts return ordinary `ActResult`s with `Channel = "api"`; recording them as steps is Workflows' job (Phase 3). The descriptor never holds clipboard text, only `<N chars>`. |
| Q9 interface home | Unchanged: all §7 interfaces are in Core. |
| Q10 safety scope | Engine names `system` and `ocr`; the host's `ToolCallAudit` writes the audit line; the user-activity pause applies only to `app_launch` and `window_manage`; the stop key applies to everything. |

## Reason codes used (no new codes)

`unsupported_action` (bad arguments), `not_found` (no such window/app/device/media session, backend failure before any input), `elevated_target`, `secure_desktop`, `policy_forbidden`, `kill_switch`, `user_active`, `foreground_failed`, `offscreen` (minimized window for OCR), `readback_mismatch`, `engine_timeout`, `input_interrupted`.

## Tests

| Project | Total | Passed | Skipped | Failed |
|---|---|---|---|---|
| `OmniWin.SystemTools.Tests` (Debug and Release) | 142 | 139 | 3 | 0 |
| `OmniWin.Ocr.Tests` (Debug and Release) | 58 | 55 | 3 | 0 |

Skipped, with reasons:
- `Live_audio_set_reads_back_and_is_restored` and `Live_clipboard_set_then_get_round_trips_and_is_restored` (`LiveSessionTests`): change the real volume / clipboard; need `OMNIWIN_LIVE_SESSION=1` and the owner present (Phase 5). They hold `Local\OmniWin.UiInputTestLock` and restore what they change.
- `Measure_system_tools_and_write_the_artifact` and `Measure_windows_ocr_on_a_1080p_image` (`Measurements`): opt-in with `OMNIWIN_WS3_BENCH=1`; both were run once (below).
- `A_screen_region_covered_by_a_mask_comes_back_black` and `A_region_capture_is_a_24_bit_bmp_of_the_requested_size_at_the_requested_origin` (`RealCaptureTests`): need an interactive desktop. In this session `OpenInputDesktop` fails and the virtual screen is 640x480 (locked or disconnected display), so BitBlt returns "invalid handle". They skip with that reason instead of failing.

Covered without a live session: argument validation, gate order (`gate` always before any `input:` entry in the call log), gate denial leaves everything unchanged, stop key before and after the gate and after input started, hung backend → `engine_timeout` with the right `input_sent`, caller cancel, backend exceptions never escape, `app_launch` input rules (paths, URLs, command lines, shell syntax, allow list, terminals, args), OCR ref store (monotonic, never reused, concurrent adds, 30 s old ids still resolve so WS1 can say `stale`, pruning), OCR service (window selection, forbidden windows, masks, region, size limit, no OCR language, timeout), text formats, real Windows OCR on an image the test draws itself (words and boxes found), the mask painter on an in-memory bitmap, and real read-only calls (system info, Start-menu list, audio read, media state).

Also checked by running the server over stdio: `tools/list` shows all nine tools with the exact parameter names; `system_info time`, a bad `kind`, a bad media action, a path to `app_launch` (`denied policy_forbidden`, `input_sent=false`), an unknown window and `apps_list` all returned the expected status lines.

**Existing test that now fails (not mine to edit):** `OmniWin.Integration.DesktopProtocolTests.Desktop_tools_work_over_stdio_and_audit_redacts_request_text` pins the exact tool list to the five desktop tools plus status; any new tool breaks it. It needs the new full list (the browser and workflow tools registered by other agents break it too).

## Performance (ms)

Artifacts: `artifacts/benchmarks/ws3-system-v1.json` (key `rows`) and `artifacts/benchmarks/ws3-ocr-v1.json`. Warm-up 20 calls, then 300 samples (50 for OCR). Gate time is excluded from the service's own metric; here a fake gate returns at once.

| Op | Budget p95 | p50 | p95 | Samples | Conditions |
|---|---|---|---|---|---|
| `audio` read | ≤ 5 | 0.074 | 0.157 | 300 | **no-device path**: this PC has no audio output device, so the real read could not be timed |
| `audio` set | ≤ 5 | 0.007 | 0.026 | 300 | fake backend: OmniWin's own pipeline only |
| `media` | ≤ 5 | 0.001 | 0.002 | 300 | fake backend, skip (no wait): pipeline only |
| `window_manage` | ≤ 5 | 0.002 | 0.003 | 300 | fake backend, move: pipeline only. Real window calls not timed |
| `screen_ocr` (Windows OCR only) | ≤ 300 | 76.74 | 117.47 | 50 | 1920x1080 image, 360 words, first call 135.2 ms. **Capture not timed** |
| `apps_list` first call | none | 411.174 | n/a | 1 | cold shell read |
| `apps_list` warm | none | 0.001 | 0.005 | 300 | cached |
| `system_info` time | none | 0.012 | 0.045 | 300 | real |
| `system_info` battery | none | 0.004 | 0.009 | 300 | real |
| `system_info` displays | none | 0.027 | 0.038 | 300 | real |
| `system_info` network | none | 25.03 | 33.337 | 300 | real, adapter list |
| `clipboard_set` / `clipboard_get` | none | 0.001 | 0.002 | 300 | fake backend: pipeline only |

`window_manage` and `audio` budgets are **not proven** against real Windows calls yet: that needs the live session (real windows, an audio device). The first `app_launch`/`window_manage` call after start is slower because it starts the shell / window listing (seen once over stdio: 459 ms and 747 ms including the desktop engine's first window scan).

## Not verified here (needs the Phase 5 live session or a machine with a display and an audio device)

- Screen-pixel capture of a window or region (`PrintWindow`, `BitBlt`) and its timing.
- `oN` click end to end through `desktop_act` (the store, the 10 s rule and the registration are tested; the click itself belongs to WS1 and needs a live window).
- Real `window_manage` on a fixture window (all nine actions), real `app_launch`, real `media` with a session, real audio set, real clipboard round trip (the last two exist as gated tests above).
- Task Manager as an elevated target.

Phase 5 tests still to write because they need the WS4 fixture app: `window_manage` on a fixture window, `app_launch` of a Start-menu app and closing it, `screen_ocr` of a fixture window at 100 % and a scaled DPI, and the `oN` click joint test with WS1.

## Adversarial cases handed to WS4 (expected status, reason, `input_sent`)

| Case | Expected |
|---|---|
| `app_launch` with a path (`C:\Windows\System32\notepad.exe`, `notepad.exe`) | `denied policy_forbidden`, `input_sent=false` |
| `app_launch` with a URL, `ms-settings:`, `cmd /c ...`, `a && b` | `denied policy_forbidden`, `input_sent=false` |
| `app_launch` of an allow-listed path | starts; any other path `denied` |
| `app_launch` of a terminal or PowerShell with `args` | `denied policy_forbidden`, `input_sent=false` |
| `app_launch` of a Start-menu app with `args` | `blocked unsupported_action`, `input_sent=false` |
| `app_launch` of an entry whose process is on the blocklist | gate: `denied policy_forbidden`, `input_sent=false` |
| `app_launch`, unknown name / ambiguous name | `blocked not_found`, `input_sent=false` |
| `window_manage close` denied by the gate | `denied`, `input_sent=false`, line has `risk=risky` |
| `window_manage` on an elevated window (Task Manager) | `blocked elevated_target`, `input_sent=false` |
| `window_manage` on OmniWin's own window | `denied policy_forbidden`, `input_sent=false` |
| `window_manage` with the user active | `blocked user_active`, `input_sent=false` |
| `window_manage` unknown action, `move` without x/y, `resize` without size | `error unsupported_action`, `input_sent=false` |
| `window_manage` ambiguous window | `blocked not_found`, `input_sent=false` |
| `clipboard_get` denied or timed out (gate reason) | `denied <gate reason>`, `input_sent=false`, `risk=risky`, no text |
| `clipboard_set` text in a log or audit line | never present; only `<N chars>` |
| Stop key tripped before the call | `denied kill_switch`, `input_sent=false` |
| Stop key tripped after input began | `error input_interrupted`, `input_sent=true` |
| Hung system call | `error engine_timeout`, `input_sent` true only if input began |
| `audio` volume outside 0..100 | `error unsupported_action`, `input_sent=false` |
| `screen_ocr` on a blocklisted or OmniWin window | `denied policy_forbidden`; inside a region the area is black |
| `screen_ocr` on an elevated window | `blocked elevated_target` |
| `desktop_act` on an `oN` ref older than 10 s | `stale` (WS1) |
| `desktop_act` on an `oN` ref whose point is covered | `blocked` (WS1) |

## Files

- `src/OmniWin.SystemTools`: `SystemToolsService.cs`, `AppLaunchRules.cs`, `AppCatalog.cs`, `AudioBackend.cs`, `MediaBackend.cs`, `ClipboardBackend.cs`, `SystemInfoProvider.cs`, `WindowOps.cs`, `SystemFormat.cs`, `Common.cs`, `NativeMethods.txt`, project file.
- `src/OmniWin.Ocr`: `OcrService.cs`, `OcrRefStore.cs`, `ScreenCapture.cs`, `OcrEngine.cs`, `OcrFormat.cs`, `NativeMethods.txt`, project file.
- `src/OmniWin.Server`: `Tools/SystemTools.cs`, `Tools/OcrTools.cs`, and the WS3 block in `Program.cs` (OcrRefStore, IOcrRefResolver, SystemToolsService, ISystemTools, OcrService, IOcrFallback, two IComponentStatus).
- `tests/OmniWin.SystemTools.Tests`, `tests/OmniWin.Ocr.Tests` (the "no tests yet, ignore exit code 8" line was removed from both project files).
- Core, Desktop, Safety, Browser, Workflows: not edited.

## Contract notes for 2.4

- `ActionDescriptor.Engine` values: `system` (system tools) and `ocr`.
- Text formats: see `SystemFormat` / `OcrFormat`; `clipboard_get` first line is `clipboard chars=N [truncated=true] risk=risky total=…` (no text), then the untrusted header, then the text.
- Q5 adapter shape (`SystemOutcome<T>`, `OcrOutcome`) is WS3-internal; the §7 interfaces are unchanged.
- No new reason codes. Ambiguous window or app is `not_found` with candidates in the detail.
- `app_launch`: Start-menu apps take no `args`; `args` only for allow-listed executables, never for a command processor or script host.

## Look-only screen view (`screen_view`), 2026-11-08
Owner decision: a look-only fallback for pictures, charts and canvas apps. It never clicks, types or moves anything, and nothing can act through it.
- **Code:** `OmniWin.Core/ViewContracts.cs` (`ViewImage`, `ViewOutcome`, `IScreenViewer`, `IBrowserViewer`, `ViewLimits`, `ViewFormat`), `OmniWin.Core/ViewImaging.cs` (mask, crop, downscale, PNG-or-JPEG whichever is smaller, using the built-in Windows imaging only), `OmniWin.Ocr/ScreenViewService.cs`, `OmniWin.Ocr/UiaPasswordLocator.cs` (UI Automation `IsPassword`, read-only), `OmniWin.Server/Tools/ViewTools.cs`.
- **Rules kept:** default is the foreground window; the whole screen only with `window: "screen"`; nothing is brought to the front; password fields, OmniWin's own, sign-in, secure-desktop and policy-blocked windows are black on the full-resolution pixels before scaling; an inaccessible (elevated) window in a screen view is blacked out whole; any masking or encoding failure returns `error` and **no image**; the stop key is checked before and after capture; no user-activity pause (no input); nothing is logged or written to disk; no new package or download.
- **Tests (new, `OmniWin.Ocr.Tests`):** `ScreenViewServiceTests` (20: foreground default and `"screen"`, gate descriptor, password rectangles black and the rest untouched, masking failure gives no image and no capture, OmniWin/blocked/inaccessible windows blacked out, downscale and 1920 clamp, window plus region crop, refused windows, stop key, gate denial, text line and metric). Project total now 82: 75 passed, 7 skipped, 0 failed.
- **Skipped:** `ScreenViewLiveTests` (3, Phase 5: fixture window with password field black, Notepad as foreground default, a known picture's colours; the model's description of the picture is checked by hand in the live session) and `ViewMeasurements` (opt-in, `OMNIWIN_VIEW_BENCH=1`). Real screen capture needs an interactive desktop, which this session does not have.
- **Measurement** (`artifacts/benchmarks/view-screen-v1.json`, keys `p50_ms`, `p95_ms`, `p99_ms`, `output_kb`): mask + downscale + encode of a 1920x1080 image with 30 lines of text, 50 samples: p50 31.98 ms, p95 37.04, p99 39.79; output 1280x720 JPEG, 139.7 KB (a rough token-cost guide for a text-heavy window). Capture is not measured (no interactive desktop), so the 300 ms report-only row is not proven end to end.
- **Detected boxes (step 4):** not built, as decided (needs a download). Plan in `docs/contract-proposals/WS3-detected-boxes.md`.

### Review fixes F3 and F4 (screen_view), Debug
- **F3:** windows past `MaxWindowsToMask` (60) were skipped and left unmasked. They are now blacked out whole. Test `Windows_past_the_search_limit_are_blacked_out_whole_never_left_unmasked` (70 windows).
- **F4:** the masks are now read before and after each capture, with the window/region geometry. If they differ the capture is repeated once; if they still differ the result is `error` and no image. Tests `A_window_that_moves_during_the_capture_is_captured_again_and_the_masks_follow_it`, `A_window_that_keeps_moving_gives_an_error_and_no_image`, `A_password_field_that_scrolls_during_a_region_capture_is_never_returned_misplaced`.
- `OmniWin.Ocr.Tests` (Debug): 86 total, 81 passed, 5 skipped, 0 failed.
