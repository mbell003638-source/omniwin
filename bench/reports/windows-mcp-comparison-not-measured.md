# OmniWin and Windows-MCP: design comparison, NOT MEASURED

No speed claim is made here. The Windows-MCP side needs Python, `uv` and Windows-MCP to be downloaded, which the owner has not approved. Every row below is **not measured: needs a download the owner hasn't approved**. The Windows-MCP column is the owner's and reviewer's description of its design, not something this project checked against its code; treat it as unverified.

| Topic | OmniWin (from this project's code) | Windows-MCP (as described, unverified) | Measured? |
|---|---|---|---|
| How it reads the screen | element lists from UI Automation and the browser page, with refs and generations; OCR and a look-only image as fallbacks | reads the screen fresh on most calls; screenshots for the model | not measured |
| Caching | keeps the last reads in memory; a live cache serves repeat observes; diffs by generation | no persistent element cache described | not measured |
| Process model | one long-running .NET program; the first call pays the start-up cost | a Python program | not measured |
| Input checks | element re-read right before input; hit test; focus check; stop key; pause while the user is typing; `input_sent` reported exactly | not compared | not measured |
| Honesty of results | `confirmed`, `unverified`, `contradicted`, `stale`, `blocked`, `denied`, `error`, and whether input was sent | not compared | not measured |
| Deletion policy | deleting is refused (names, ids, typed commands); nothing else is blocked | not compared | not measured |
| Abilities OmniWin lacks | drag and drop; clicking a raw screen position (refused by design); a run-any-command tool (absent by design); a screenshot that can be acted on | has some or all of these (as described) | not applicable |
| Abilities Windows-MCP lacks | exact `input_sent`; no-delete rule; per-element identity checks; dedicated browser profile with page-level hit tests | not compared | not applicable |

## Expectation (unmeasured, not a result)

OmniWin should be faster per action because it stays running and keeps what it has read. In both, the model's thinking time between calls probably dominates. Do not repeat this as a finding until both sides are measured.

## OmniWin's half of the speed test (ready)

The 9-task harness (B01 to B07, B09, B10; B08 dropped because it changes a setting) is built in `bench/OmniWin.Bench`. It runs in the live session and writes to `bench/reports/`. The browser task B09 has already run headless 20 times (see `docs/status/WS4.md`).

## Steps to measure Windows-MCP later (needs the owner's yes first)

1. Download and install Python 3 and `uv` (about 100 to 150 MB together; check the sizes on the download pages at the time).
2. Fetch Windows-MCP from its project page into a separate folder such as `C:\Users\<you>\Desktop\windows-mcp-baseline\` (a few MB plus the dependencies `uv` installs into that folder).
3. Start it as an MCP server the same way a client would, and run the same 9 tasks with the same independent checks, three or more times each, on the same PC with the same apps.
4. Record per-task wall time, number of calls, and wrong-target or false-success counts in `bench/reports/`, with the Windows-MCP version.
5. Fill in the "Measured?" column only from those numbers.
