# WS1 functional acceptance — 2026-11-07

All ten owner workflows passed through the running OmniWin MCP server. The fixture and Explorer deletion-refusal checks also passed with `status=denied` and `input_sent=false`. Strict input-latency acceptance remains open; functional passes do not establish speed compliance.

## Environment and evidence

The server used Release, real stdio MCP calls, `test_mode=false`, the no-delete SafetyGate, and armed stop/activity hooks. Chrome tasks used desktop UI Automation because WS2 is pending. The owner disabled Smart App Control before these tests; no security settings were changed by the test code.

The ad hoc client initially launched the DLL with `dotnet`, which reported DPI-unaware mode. This was corrected to the supported manifest-aware `OmniWin.Server.exe`; final status reports `dpi=per_monitor_v2`. The EXE recheck confirmed Notepad/Explorer switching, selected-item and menu focus, and physical File-menu expansion. The broader ten-workflow transcript predates this launcher correction; it establishes functional results on this desktop, not multi-DPI coverage.

Raw local evidence is retained under `artifacts/acceptance/2026-11-07T13-10-57-984Z/`: `transcript.json`, `results.json`, and `logs/audit-20261107.jsonl`. Window IDs and element references are session-local; the transcript spans server restarts while correcting issues. Do not reuse its IDs. Raw observations can contain unrelated window names, so they are not copied into this tracked report.

Actual acceptance input went through OmniWin. Read-only external screenshots/window inspection helped diagnose provider and foreground behavior. Fixture setup and creation of the dedicated move source were test preparation, not substitutes for the GUI actions under test.

## Owner workflow results

| # | Workflow | Result and verification |
|---|---|---|
| 1 | Open Notepad, type, save to Desktop | Pass. Started from Windows Start search, used a new tab, typed mixed Unicode, committed the Save As filename, and verified saved UTF-8 contents. |
| 2 | Launch an installed app by Start-menu name | Pass. Ctrl+Escape, search `Notepad`, Enter; the expected process/window opened. |
| 3 | Open Chrome | Pass. Windows Start search opened a new Chrome window. |
| 4 | YouTube video search | Pass. Invoked Home/Search controls, set the search field to `Windows accessibility tutorial`, submitted, and read back the results URL/title and video links. |
| 5 | Google Maps route | Pass. Directions from Toronto City Hall to Royal Ontario Museum returned resolved endpoints and route options. No personal location was requested. |
| 6 | Move Downloads file to Desktop in Explorer | Pass. Selected the dedicated test file, Ctrl+X, navigated to Desktop, Ctrl+V; source disappeared and destination bytes matched. |
| 7 | Type and read back text | Pass. Real Notepad received ASCII, accented text, Greek, em dash, and emoji; exact document text and saved bytes matched. |
| 8 | Click a button/menu and verify | Pass. A physical click expanded Notepad's File menu, confirmed by fresh UIA state. The fixture Apply counter also verified physical clicks. |
| 9 | Select a word and replace it | Pass. Notepad navigation/selection keys selected `acceptance`; typing replaced only that word with `verified`, then exact full-text and saved-file checks passed. |
| 10 | Switch between open windows | Pass. Completed build switched Notepad → Explorer → Notepad; foreground IDs and subsequent physical clicks proved the requested frames were visible. Chrome switching was also exercised earlier. |

The Explorer refusal was checked before moving the dedicated source file: Delete returned denied without input and the source still existed. The fixture's Delete control was also denied without input. These checks exercise the implemented policy; the lexical delete filter remains a heuristic rather than a sandbox for arbitrary scripts.

An additional attempt to focus Explorer's outer Desktop pane reported `contradicted`: Explorer delegated keyboard focus to its selected file. Targeting that actual selected item then confirmed. A generic File-menu click reported `unverified`, followed by a fresh observation proving expansion. These results were retained rather than relabeled as confirmed actions.

Final Debug/Release solution builds passed with warnings as errors and zero warnings/errors. Each normal test run passed 406 tests, failed zero, and skipped the three opt-in UI scenarios. The functional fixture and repeated-input diagnostic scenarios passed separately.

The final [validation report](../benchmarks/WS1_Final_Validation_2026-11-07.md) records the EXE-based reruns, individual benchmark evidence, hashes, read-only startup tails, and remaining latency failures. The final live fixture used the test EXE and passed; its console result is `final-apphost-fixture.txt` in the raw acceptance directory.

## Bugs exposed and corrected

- **Shell discovery:** native `EnumWindows` can omit shell/UWP surfaces. Discovery now merges native inventory, the foreground root, and known handles, and rescans UIA on events, explicit fresh reads, or unavailable event handlers. Fresh visibility/process/ownership checks still apply.
- **Missing dialog controls:** filtering immediate raw children lost controls behind non-control containers in Save As. Bounded ControlView tree walking now reaches filename and file-list controls while retaining visibility, clipping, identity, depth, and node caps.
- **Notepad Unicode corruption:** the installed Notepad's `RichEditD2DPT` provider repeated the last character of a burst. That class now receives one Unicode scalar per checked batch; other controls retain 16-unit batches. Surrogate pairs stay intact. There is no automatic replay or clipboard fallback.
- **WinUI foreground identity:** Windows reported the focused RichEdit child as foreground. Foreground ownership now uses its top-level root. Validated provider focus plus native activation can raise the matching frame without moving focus to an unrelated control.
- **Activation boundaries/accounting:** every mutating restore/activation/frame-raise step checks the live stop/activity/desktop boundary, including after the bounded activation wait. Completed earlier activation remains recorded if a later boundary stops the operation.

A UIA `set_value` filename readback alone did not commit the Save As dialog's application-level choice. The successful workflow typed the full path and pressed Tab before Save. Tool documentation now calls out this distinction. An early attempt created the extra test file listed below; it was preserved under the owner's deletion policy.

## Files left for inspection

- `C:\Users\<you>\Desktop\OmniWin_Acceptance_Notepad_2026-11-07T13-10-57-984Z.txt`
- `C:\Users\<you>\Desktop\OmniWin_Acceptance_Move_2026-11-07T13-10-57-984Z.txt`
- Extra test output: `C:\Users\<you>\Documents\OmniWin acceptance note — café Ω 🚀.txt`

The final Desktop Notepad document is `OmniWin verified note — café Ω 🚀. ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789.` The move fixture contains only its unique acceptance-run marker. No personal Downloads file was moved.

## Remaining scope

The functional WS1 acceptance list is complete. WS2 browser/CDP, WS3 system/OCR, broad multi-monitor/hung-provider coverage, and strict latency compliance remain separate work. Physical input still requires an active, unlocked interactive session and no competing user input. Explicit supported UIA patterns can operate on covered windows, subject to provider support and the existing checks; physical input cannot safely share one desktop with arbitrary simultaneous user activity.
