# OmniWin — Conversation Summary (2026-11-06)

A summary of the conversation between the owner and the previous AI (Claude, in Claude Code), written so another AI has the context. It is a summary, not the raw transcript: the raw log may contain the two pieces an automatic safety check stopped, and those are deliberately left out.

---

## 1. What the owner asked for, in order

| # | Owner's request | Outcome |
|---|---|---|
| 1 | Review `OmniWin_Master_Plan_and_AI_Prompt.md` (v1) | Reviewed. Main problems: overclaims ("zero-hallucination", "hardware-level" input, "immune to DPI"), a PowerShell script tool behind a blocklist, model-written scripts saved as tools, driving the logged-in Chrome over port 9222 (Chrome 136+ ignores that flag on the default profile), and a one-shot "production codebase" from a chat model. |
| 2 | Re-analyse after another agent added GitHub links | Re-checked. The linked projects (Computah, macbrow) are small experimental macOS voice-control repos, not proven systems. Windows-MCP already covers most of the surface. |
| 3 | "How would you approach it, with execution time in micro/milliseconds and accurate?" | Explained: one long-running process, cached UI Automation state refreshed by events, batched reads, event-driven waits, server-side expectations so the model is called less often, replay of approved workflows. Model think time (0.5–3 s) and app reaction time dominate, not the engine. |
| 4 | `/goal`: rewrite the plan correctly, spawn multiple agents, build a handoff, save it to the Desktop | Done. The plan was rewritten as v2 (v1 is archived). The handoff folder holds the contract and the orchestration plan. Four agents wrote the WS1–WS4 briefs in `agents\`, and each was checked on disk. |
| 5 | "Update the plan doc" | Done. Plan §7 and the README list the briefs and the contract gaps the agents found. |
| 6 | "Start building" | Installed the .NET 10 SDK; it waited about 2 hours on a Windows admin prompt until the owner approved it. Built **M0** and verified it (§3). |
| 7 | "What input tools?" / "What next?" / "Explain like I'm 5" | Explained that 12 of the 24 tools act (click, type, keys, scroll, browser actions, launch, window management, audio, media, clipboard, steps/workflows) and 12 only read. Recommended building the safety layer next, plus a baseline benchmark on the owner's real tasks. |
| 8 | "Yes, commit; start building the hands; no rules needed apart from deleting; it can send, edit, do everything but delete." Tasks: "you decide: Notepad, running apps, Chrome, moving files, typing, clicking, editing, YouTube, Google Maps routes" | Committed M0 (`3da6c1a`). Wrote the delete-only safety pieces (§4). |
| 9 | "No terminal block" | Removed the terminal/shell block. Typed delete commands are still blocked in places that run commands. |
| 10 | Building the desktop engine | **Stopped by the safety check** (§5). |
| 11 | "I have given the approvals, go ahead, force what was stopped" | Declined: the owner's approval can't override that check. |
| 12 | "Prepare handoff … I will do it with other AI" and "Save the conversation to the folder" | Wrote `04_Status_Handoff.md` and this file. |

---

## 2. Decisions

**Product (from the v2 plan and the contract):**
- C#/.NET 10, an MCP server over stdio (official `ModelContextProtocol` SDK), stdout used only for JSON-RPC.
- Desktop through UI Automation, browser through a thin CDP client on a dedicated profile, system actions through direct Windows APIs.
- No shell or script tool; workflows are data only.
- Element refs with generations; results always state `input_sent`; never repeat uncertain input.

**Build:**
- Workstreams WS0–WS4 with one owner per file, a branch and worktree per stream, milestones M0–M5.
- The `Local\OmniWin.UiInputTestLock` mutex for any test that sends real input.

**Contract 2.1 (M0), settling the gaps the agents found:**
- all interfaces live in `OmniWin.Core`;
- `input_interrupted` covers partial input;
- the target is re-checked after a user confirmation;
- `--data-dir` override;
- `IComponentStatus`, so `omniwin_status` can report "browser attached".

**Owner's safety policy (overrides contract §13/§14):**
- no confirmation prompts;
- only deleting is blocked;
- no terminal block;
- the stop key (Ctrl+Alt+Shift+K) and the audit log stay.

Full details are in `04_Status_Handoff.md` §5.

---

## 3. M0 (committed `3da6c1a`, verified)

- All 17 projects from contract §4 build with 0 warnings; 30/30 unit tests pass.
- The stdio server answers `omniwin_status`:
  - 0 non-JSON lines on stdout over 300 calls;
  - DPI awareness reported as `per_monitor_v2`;
  - clean exit when stdin closes;
  - startup refusals return exit codes 2, 3 and 4 as designed.
- Timings: status handler about 0.035 ms p50, stdio round trip about 0.42 ms p50, warm startup about 305 ms.

Problems hit and fixed during M0:
- PowerShell `try` inside parentheses isn't valid syntax.
- Shell quoting mangled backslashes in generated project files, so they were rewritten from PowerShell.
- Raw U+2028/U+2029 characters ended up in the source as real characters; replaced with char codes.
- The CsWin32 DPI constant lives on the `DPI_AWARENESS_CONTEXT` struct, not on `PInvoke`.
- .NET 10 dropped VSTest for xunit.v3; switched to Microsoft.Testing.Platform in `global.json`.
- Empty test projects need `--ignore-exit-code 8` until they have tests.
- The JSON writer escaped `+` as `\u002B`; switched to relaxed escaping.
- The MCP SDK logged two lines per request; filtered to warnings.

---

## 4. After M0 (uncommitted, compiles, untested)

`DeletePolicy`, `SafetyGate`, `KillSwitch` and `AuditLog` in `src/OmniWin.Safety`, plus config changes (`confirm_mode` "off", `blocked_words_extra`). Nothing registers or calls them yet. See `04_Status_Handoff.md` §4.

---

## 5. What the safety check stopped

An automatic safety check reviews the previous AI's responses. It stopped two responses, both about the desktop engine that sends clicks and keystrokes:

1. A detailed brief for that engine (before M0).
2. The engine's code, partway through, after the owner had asked for no rules except no-deleting and no terminal block.

Each time it said not to produce that content again, even reworded, so the previous AI didn't continue that piece. The existing `agents\WS1_Desktop_Engine.md` brief, written earlier by a separate agent, wasn't affected and is unchanged.

---

## 6. Where everything is

| What | Where |
|---|---|
| Plan v2 | `C:\Users\<you>\Desktop\OmniWin_Master_Plan_and_AI_Prompt.md` |
| Plan v1 (archived) | `OmniWin_Handoff\archive\OmniWin_Master_Plan_v1_original.md` |
| Contract 2.1 (source of truth) | `OmniWin_Handoff\02_Architecture_and_Contracts.md` |
| Orchestration, kickoff prompts | `OmniWin_Handoff\03_Build_Orchestration.md` |
| Current state, owner decisions, next steps | `OmniWin_Handoff\04_Status_Handoff.md` |
| Workstream briefs | `OmniWin_Handoff\agents\WS1…WS4` |
| Code | `C:\Users\<you>\Desktop\OmniWin` (branch `main`) |
| WS0 status and measurements | `OmniWin\docs\status\WS0.md` |
