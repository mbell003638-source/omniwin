# Learned tasks (starter recipes)

Taught on the reference PC on 2026-11-17 (Windows 11, English (Canada), OmniWin 2.6) by a scripted teacher driving OmniWin's own tools
(`bench/teach/teach.py`, task list `bench/teach/make_tasks.py`). Each task was done once, saved with `task_done`, replayed three
times with other values, then run from new words; every other wording was then run once so OmniWin learned it.

- Tasks learned: **85** of 94 tried; wordings learned in addition to each task's first one: **582**.
- First run (scripted, so no AI thinking time is included): median 1681 ms.
- Instant run from words (`omniwin_do`, one call): median 1494 ms, 59 tasks.
- Web tasks (Google, Flights, Hotels, Maps, YouTube, Wikipedia) never run by themselves (owner rule: no browser step at speed 1);
  the AI runs them in one call with `workflow_run`. They search only: nothing is booked, bought, signed in to or sent.
- Left out on purpose: Bluetooth on/off, brightness, camera, notifications (system settings, privacy, or different every time),
  terminal commands (a learned paste into a terminal would skip OmniWin's typed-delete check), Word and Excel (not installed).

| Task | Group | Result | Wordings learned | Instant run | Recipe |
|---|---|---|---|---|---|
| calc-sum | Calculator | learned | 8 of 8 | 744 ms | `calculate-12-7-in-calculator` |
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
| explorer-documents | File Explorer | learned | 11 of 14 | 2314 ms | `show-my-documents-folder` |
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
| notepad-note | Notepad | learned | 1 of 8 | yes | `` |
| settings-windows-update | Settings | learned | 1 of 26 | yes | `` |
| media-home | Media Player | learned | 11 of 14 | 978 ms | `show-media-player-home` |
| media-music-library | Media Player | learned | 11 of 14 | 944 ms | `show-my-music-library` |
| media-video-library | Media Player | learned | 11 of 14 | 931 ms | `show-my-video-library` |
| media-play-queue | Media Player | learned | 11 of 14 | 944 ms | `show-the-play-queue` |
| media-playlists | Media Player | learned | 11 of 14 | 933 ms | `show-my-playlists` |
| weather-forecast | Weather | learned | 11 of 14 | 583 ms | `show-the-weather-forecast` |
| weather-hourly-forecast | Weather | learned | 11 of 14 | 575 ms | `show-the-hourly-forecast` |
| weather-monthly-forecast | Weather | learned | 11 of 14 | 610 ms | `show-the-monthly-forecast` |
| weather-maps | Weather | learned | 11 of 14 | 523 ms | `show-the-weather-maps` |
| weather-historical-weather | Weather | learned | 11 of 14 | 772 ms | `show-the-historical-weather` |
| weather-pollen | Weather | learned | 11 of 14 | 620 ms | `show-the-pollen-forecast` |
| weather-life | Weather | learned | 11 of 14 | 716 ms | `show-the-weather-life-tips` |
| weather-favorites | Weather | learned | 11 of 14 | 592 ms | `show-my-favorite-weather-places` |
| explorer-home | File Explorer | learned | 11 of 14 | 1779 ms | `show-explorer-home` |
| explorer-gallery | File Explorer | learned | 11 of 14 | 1762 ms | `show-the-photo-gallery` |
| explorer-network | File Explorer | learned | 11 of 14 | 1836 ms | `show-network-places` |
| explorer-this-pc | File Explorer | learned | 11 of 14 | 1821 ms | `show-this-pc` |
| settings-home | Settings | learned | 11 of 14 | 1776 ms | `show-the-settings-home-page` |
| clock-settings | Clock | learned | 11 of 14 | 792 ms | `show-the-clock-settings` |
| media-settings | Media Player | learned | 11 of 14 | 1031 ms | `show-the-media-player-settings` |
| explorer-recent | File Explorer | learned | 11 of 14 | 1494 ms | `show-my-recent-files` |
| explorer-favorites | File Explorer | learned | 11 of 14 | 1430 ms | `show-my-favorite-files` |
| explorer-shared | File Explorer | learned | 11 of 14 | 1419 ms | `show-files-shared-with-me` |
| explorer-downloads | File Explorer | not_learned | 0 of 26 |  | `` |
| store-home | Microsoft Store | learned | 11 of 14 | 3792 ms | `show-the-store-home-page` |
| store-apps | Microsoft Store | learned | 11 of 14 | 3848 ms | `show-apps-in-the-store` |
| store-gaming | Microsoft Store | learned | 11 of 14 | 2290 ms | `show-games-in-the-store` |
| store-library | Microsoft Store | learned | 11 of 14 | 2314 ms | `show-my-store-library` |
| store-themes | Microsoft Store | learned | 11 of 14 | 3045 ms | `show-themes-in-the-store` |
| photos-gallery | Photos | learned | 11 of 14 | 1619 ms | `show-my-photo-gallery` |
| photos-favorites | Photos | learned | 11 of 14 | 1650 ms | `show-my-favorite-photos` |
| photos-this-pc | Photos | learned | 11 of 14 | 1658 ms | `show-photos-on-this-pc` |
| store-whats-new | Microsoft Store | learned | 11 of 14 | 2496 ms | `show-what-is-new-in-the-store` |
| paint-red | Paint | learned | 1 of 14 | AI only | `show-paint-with-red` |
| paint-green | Paint | learned | 1 of 14 | AI only | `show-paint-with-green` |
| paint-black | Paint | learned | 1 of 14 | AI only | `show-paint-with-black` |
| paint-yellow | Paint | learned | 1 of 14 | AI only | `show-paint-with-yellow` |
| youtube-search | Web | learned | 1 of 6 | AI only | `search-youtube-for-lofi-music-6` |
| wikipedia | Web | learned | 1 of 6 | AI only | `look-up-canada-on-wikipedia-2` |
| google-maps | Web | learned | 1 of 6 | AI only | `find-cn-tower-on-google-maps` |
| settings-display | Settings | learned | 11 of 14 | 2137 ms | `show-display-and-brightness-settings` |
| settings-sound | Settings | learned | 11 of 14 | 2314 ms | `show-sound-settings` |
| settings-notifications | Settings | learned | 11 of 14 | 2332 ms | `show-notification-settings` |
| settings-power | Settings | learned | 1 of 14 | AI only | `show-power-and-battery-settings` |
| settings-storage | Settings | learned | 11 of 14 | 2278 ms | `show-storage-settings` |
| notification-center | Windows | not_learned | 0 of 14 |  | `` |
| web-search | Web | learned | 1 of 8 | AI only | `search-the-web-for-weather-toronto-4` |
| google-flights | Web | teach_failed | 0 of 6 |  | `` |
| google-hotels | Web | teach_failed | 0 of 6 |  | `` |
| terminal-ip | Terminal | teach_failed | 0 of 14 |  | `` |
| terminal-hostname | Terminal | not_learned | 0 of 14 |  | `` |
| terminal-whoami | Terminal | not_learned | 0 of 14 |  | `` |
| terminal-dir | Terminal | teach_failed | 0 of 14 |  | `` |
| terminal-tasklist | Terminal | teach_failed | 0 of 14 |  | `` |

Failures are listed as they happened (`artifacts/benchmarks/curriculum-*.json` has each step's status).
