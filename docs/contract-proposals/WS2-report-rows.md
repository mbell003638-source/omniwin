# Proposal: WS2-report-rows (Q16)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Track report-only rows in `docs/status/WS2.md` alongside budgeted rows.

## Problem
In contract §15, budgets are set for `browser_observe` from live index (≤ 10 ms) and `browser_act` excluding page reaction (≤ 10 ms). The brief also mentions measuring cold observe (first read after navigation) and browser launch to first observation as report-only metrics.

## Proposed Change
1. Include report-only rows in `docs/status/WS2.md`:
   - `browser_observe, cold (report)`: No formal budget, tracking cold read latency.
   - `launch to first observe (report)`: No formal budget, tracking cold process start and initial attach.
2. In milestone benchmark runs, capture and record these timings alongside the budgeted rows.

## Impact on Other Streams
- WS0: Full visibility into startup and cold navigation characteristics.
- WS2: Reported in `docs/status/WS2.md`.
