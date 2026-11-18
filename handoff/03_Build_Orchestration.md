# OmniWin — Build Orchestration

How to run the build with several builder agents in parallel. Read after `02_Architecture_and_Contracts.md`. The contract wins on any conflict.

---

## 1. Roles

| Stream | Owner role | Owns (see contract §4 for exact paths) |
|---|---|---|
| **WS0** | Lead / integrator | Repo skeleton, `OmniWin.Core`, server host, `OmniWin.Safety`, `OmniWin.Workflows`, contract changes, merges |
| **WS1** | Desktop engine | `OmniWin.Desktop`, `Tools/DesktopTools.cs`, its tests |
| **WS2** | Browser engine | `OmniWin.Browser`, `Tools/BrowserTools.cs`, its tests |
| **WS3** | System tools + OCR | `OmniWin.SystemTools`, `OmniWin.Ocr`, `Tools/SystemTools.cs`, `Tools/OcrTools.cs`, its tests |
| **WS4** | Tests + benchmarks | `testapps/`, `tests/OmniWin.Integration/`, `bench/` |

**Rule:** every path has exactly one owner. Never edit a file you don't own. If you need a change elsewhere, write a proposal (§6).

---

## 2. Milestones and dependencies

```
M0 Skeleton (WS0) ──┬──> M1 Engines standalone (WS1, WS2, WS3, WS0-safety)
WS4 fixtures ───────┘          │
                               ▼
                      M2 Integrated through MCP (all) ──> M3 Hardening (all)
                                                             │
                                                             ▼
                                              M4 Workflows + run_steps (WS0)
                                                             │
                                                             ▼
                                               M5 Release (WS0, WS4)
```

| Milestone | Done when |
|---|---|
| **M0** | WS0 has created the repo, solution, and **all empty projects**; Core contracts compile; the server answers `omniwin_status`. WS4 has started fixtures. |
| **M1** | Each engine works on its own against fixtures; Safety gate, confirm dialog, and kill switch work; WS4 has baseline numbers. |
| **M2** | All tools are registered; WS4's end-to-end suite runs over stdio; first performance report. |
| **M3** | Adversarial suite passes; performance budgets met, or exceptions approved by WS0. |
| **M4** | Run log, `workflow_save`/`workflow_run`, and `run_steps` work. |
| **M5** | Packaged build; registered in your MCP clients; docs complete. |

WS1, WS2, and WS3 can start coding in their own folders against the contract **immediately**. They build and integrate once M0 lands. WS0 creates every project at M0, so nobody else ever edits the `.sln` file.

---

## 3. Branches and worktrees

- Each stream works on its own branch: `ws0-lead`, `ws1-desktop`, `ws2-browser`, `ws3-system`, `ws4-tests`.
- Use a separate git worktree per stream so agents never share a working directory.
- WS0 merges into `main` in this order at each milestone: WS0 → WS4 fixtures → WS3 → WS1 → WS2.
- Before every merge: `dotnet build -warnaserror` and `dotnet test` must pass on the branch.

---

## 4. Shared-desktop rule

Tests that send real mouse or keyboard input take over the one physical desktop. Two agents running them at once will collide.

- Any test that sends real input must hold the named mutex `Local\OmniWin.UiInputTestLock` for its duration.
- Only one stream runs input tests at a time.
- Don't use the PC while those tests run.
- These tests need an interactive desktop session. They can't run in headless CI, so run them locally.

---

## 5. Status reporting

Each stream keeps `docs/status/WSx.md` current with:

- done / in progress / blocked;
- measured p50 and p95 for the operations it owns (contract §15);
- open proposals.

WS0 reads all status files at each milestone, before merging.

---

## 6. Contract changes

- To change the contract, write `docs/contract-proposals/WSx-<topic>.md`: the problem, the proposed change, and the effect on other streams.
- WS0 accepts or rejects it within the same milestone, updates the contract, and bumps `ContractVersion` in `OmniWin.Core`.
- Until a proposal is accepted, code to the current contract (add an adapter if needed).

---

## 7. Kickoff prompts

Start each builder agent with its stream prompt. Give every agent the whole `OmniWin_Handoff` folder.

**WS0 (lead):**
> You are WS0, lead for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`, and this file. Deliver M0 first: repo, solution, all empty projects, compiling Core contracts, and a server that answers `omniwin_status`. Then build Safety (gate, confirm dialog, kill switch, audit log). You own the contract and the merges.

**WS1 / WS2 / WS3:**
> You are WS<n> for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`, and this file. Your scope and owned files are in contract §4 and §7–§8. Code only inside the files you own. Meet the budgets in contract §15. Report in `docs/status/WS<n>.md`.

**WS4:**
> You are WS4 for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`, and this file. First, benchmark Windows-MCP on about 10 real tasks to set a baseline. Then build the fixture app and web fixtures, the end-to-end suite over MCP stdio, and the latency benchmarks. Hold `Local\OmniWin.UiInputTestLock` for any test that sends real input.

---

## 8. Final acceptance (WS0 checks at M5)

- Accuracy gates in contract §15 met: **zero** wrong-target actions and **zero** false `confirmed` results across the suite.
- Every adversarial case returns the expected status and `input_sent` value.
- Performance budgets met or exceptions documented.
- No tool runs scripts, shells, or arbitrary executables.
- The audit log records every tool call.
