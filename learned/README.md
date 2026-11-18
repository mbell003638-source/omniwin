# Learned tasks (starter recipes)

Taught on the reference PC on 2026-11-17 (Windows 11, English (Canada), OmniWin 2.6) by a scripted teacher driving OmniWin's own tools
(`bench/teach/teach.py`, task list `bench/teach/make_tasks.py`). Each task was done once, saved with `task_done`, replayed three
times with other values, then run from new words; every other wording was then run once so OmniWin learned it.

- Tasks learned: **38** of 44 tried; wordings learned in addition to each task's first one: **222**.
- First run (scripted, so no AI thinking time is included): median 1621 ms.
- Instant run from words (`omniwin_do`, one call): median 1462 ms, 23 tasks.
- Web tasks (Google, Flights, Hotels, Maps, YouTube, Wikipedia) never run by themselves (owner rule: no browser step at speed 1);
  the AI runs them in one call with `workflow_run`. They search only: nothing is booked, bought, signed in to or sent.
- Left out on purpose: Bluetooth on/off, brightness, camera, notifications (system settings, privacy, or different every time),
  terminal commands (a learned paste into a terminal would skip OmniWin's typed-delete check), Word and Excel (not installed).

| Task | Group | Result | Wordings learned | Instant run | Recipe |
|---|---|---|---|---|---|
| calc-sum | Calculator | learned | 8 of 8 | 744 ms | `calculate-12-7-in-calculator` |
| notepad-note | Notepad | saved_but_replays_failed | 0 of 8 |  | `write-a-note-saying-call-the-dentist` |
| settings-search | Settings | learned | 6 of 6 | 1704 ms | `search-settings-for-display` |
| settings-system | Settings | learned | 11 of 14 | 1390 ms | `show-system-settings` |
| settings-bluetooth-devices | Settings | learned | 11 of 14 | 1649 ms | `show-bluetooth-settings` |
| settings-network-internet | Settings | learned | 11 of 14 | 1209 ms | `show-network-settings` |
| settings-personalization | Settings | learned | 11 of 14 | 1630 ms | `show-personalization-settings` |
| settings-apps | Settings | learned | 11 of 14 | 1567 ms | `show-app-settings` |
| settings-accounts | Settings | learned | 11 of 14 | 1512 ms | `show-account-settings` |
| settings-time-language | Settings | learned | 11 of 14 | 1529 ms | `show-time-and-language-settings` |
| settings-gaming | Settings | learned | 11 of 14 | 1194 ms | `show-gaming-settings` |
| settings-accessibility | Settings | learned | 11 of 14 | 1284 ms | `show-accessibility-settings` |
| settings-privacy-security | Settings | learned | 11 of 14 | 1716 ms | `show-privacy-settings` |
| settings-windows-update | Settings | saved_but_replays_failed | 0 of 14 |  | `show-windows-update-settings` |
| explorer-documents | File Explorer | learned | 11 of 14 | 2314 ms | `show-my-documents-folder` |
| explorer-downloads | File Explorer | not_learned | 0 of 14 |  | `` |
| explorer-pictures | File Explorer | learned | 11 of 14 | 1416 ms | `show-my-pictures-folder` |
| explorer-music | File Explorer | learned | 11 of 14 | 2278 ms | `show-my-music-folder` |
| explorer-videos | File Explorer | learned | 11 of 14 | 1833 ms | `show-my-videos-folder` |
| explorer-desktop | File Explorer | learned | 11 of 14 | 2153 ms | `show-my-desktop-folder` |
| clock-stopwatch | Clock | learned | 11 of 14 | 617 ms | `show-the-stopwatch` |
| clock-timer | Clock | learned | 11 of 14 | 666 ms | `show-the-timer` |
| clock-alarm | Clock | learned | 11 of 14 | 609 ms | `show-my-alarms` |
| clock-world-clock | Clock | learned | 11 of 14 | 1280 ms | `show-the-world-clock` |
| clock-focus-sessions | Clock | learned | 11 of 14 | 626 ms | `show-focus-sessions` |
| calc-standard | Calculator | learned | 1 of 14 | AI only | `show-the-standard-calculator` |
| calc-scientific | Calculator | learned | 11 of 14 | 1462 ms | `show-the-scientific-calculator` |
| calc-graphing | Calculator | learned | 1 of 14 | AI only | `show-the-graphing-calculator` |
| calc-programmer | Calculator | learned | 1 of 14 | AI only | `show-the-programmer-calculator` |
| calc-date | Calculator | learned | 1 of 14 | AI only | `show-the-date-calculator` |
| calc-currency | Calculator | learned | 1 of 14 | AI only | `show-the-currency-converter` |
| calc-volume | Calculator | learned | 1 of 14 | AI only | `show-the-volume-converter` |
| calc-length | Calculator | learned | 1 of 14 | AI only | `show-the-length-converter` |
| calc-weight | Calculator | learned | 1 of 14 | AI only | `show-the-weight-converter` |
| calc-temperature | Calculator | learned | 1 of 14 | AI only | `show-the-temperature-converter` |
| calc-energy | Calculator | learned | 1 of 14 | AI only | `show-the-energy-converter` |
| calc-area | Calculator | learned | 1 of 14 | AI only | `show-the-area-converter` |
| calc-speed | Calculator | learned | 1 of 14 | AI only | `show-the-speed-converter` |
| calc-time | Calculator | learned | 1 of 14 | AI only | `show-the-time-converter` |
| calc-power | Calculator | learned | 1 of 14 | AI only | `show-the-power-converter` |
| calc-data | Calculator | learned | 1 of 14 | AI only | `show-the-data-converter` |
| google-maps | Web | teach_failed | 0 of 6 |  | `` |
| youtube-search | Web | saved_but_replays_failed | 0 of 6 |  | `search-youtube-for-lofi-music` |
| wikipedia | Web | teach_failed | 0 of 6 |  | `` |

Failures are listed as they happened (`artifacts/benchmarks/curriculum-*.json` has each step's status).
