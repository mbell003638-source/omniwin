# WS1 review follow-up — 2026-11-07

This follows `OmniWin_Handoff/06_WS1_Review_Findings_2026-11-07.md`. The owner approved D1, limited to verified native editors, and authorized live regression/acceptance. Windows reports Admin in RDP session 2; the owner confirmed they are using an RDP client. A physical-console comparison has not been performed. No budgets or deletion/terminal submission rules were changed.

## F1: validated scope and rejected Notepad experiments

The retained shortcut applies to non-password Edit/WindowsForms native editor controls whose actual class, PID, root, and handle-to-UIA mapping identify the exact target. The first batch keeps the full UIA/native check. Subsequent batches require exact native focus HWND, foreground/root, PID, and class. Browser, windowless, unverified, and command controls retain full checks. Deterministic tests cover sibling focus, another foreground window, ownership/PID/class changes, unavailable focus, and mandatory full-check fallbacks.

Direct native-only Notepad trials initially produced exact text but later corrupted a varied 100-unit sample into repeated final characters. The engine reported `contradicted`, and the run stopped without replaying input. The native focus remained correct: focus identity alone does not establish that this provider processed earlier Unicode packets.

Two caret-acknowledgement experiments observed exact collapsed `EM_GETSEL` progress before allowing the next scalar. One used full UIA backpressure; another yielded outside the MTA while retaining the input lock, aggregate dispatch budget, fresh tokens/guards, and worker-owned COM state. Initial exact Unicode readback passed, but the asynchronous trial still took 1.686–1.920 seconds across four 100-unit samples, with 1.457–1.631 seconds charged to prechecks. It was initially removed for lack of a demonstrated speed gain.

A subsequent final-run attempt showed that the older full-focus-check path also corrupted a fresh 100-character Notepad window. Caret acknowledgement was therefore restored as a correctness requirement for the verified RichEdit provider, not a claimed speed improvement. A later repeated trial also exposed a surrogate-pair failure despite correct caret progress: an emoji became two replacement characters. Supplementary Unicode and CR/LF text in this provider's physical `type` path now fail before input with `unsupported_action`; explicit `set_value` and separate Enter key actions remain available. Standard native Edit controls retain physical Unicode typing. Final acceptance must state this application-specific limitation.

Raw initial trials and the temporarily rejected source are preserved in `artifacts/acceptance/review-2026-11-07T18-28-40-427Z/`: `notepad-100-samples.json`, `notepad-ack-100-samples.json`, `notepad-async-100-samples.json`, `transcript.json`, and `rejected-packet-ack-source/`. The incomplete `final-review-2026-11-08T01-19-53-802Z/` record preserves the fresh-window failure and `notepad-ack2ms-samples.json` preserves the later surrogate-pair failure. It is explicitly marked incomplete and is not F6 acceptance. One early native-only action exceeded the default readback deadline although a subsequent observation matched; an acknowledged-path setup attempt stopped after provider focus input before typing. F1's Notepad performance requirement remains open.

## F3: frame-raise accounting

`RaiseFrame` now distinguishes API success from observed z-order change. Complete identical native-order snapshots do not record input. Changed, incomplete, or concurrently changing snapshots conservatively record possible input; earlier restore/activation remains recorded. Native traversal is bounded and includes owned/topmost relationships. Boundaries remain immediately before mutation and after read-only snapshot collection.

Regression tests use the production final-result factory: denied/blocked refusals after a proven no-op preserve `input_sent=false`; actual movement preserves `error/input_interrupted` with input sent. Snapshot equality establishes observed net order, not an atomic history; unrelated concurrent changes can conservatively count as input.

## F4: large-window observations

Each target used 20 forced fresh tree reads after discovery and 200 normal reads, with 1000 rendered elements permitted. Results below are full call duration, not just the internal snapshot timer. All tests were read-only after GUI staging.

| Target | Cold p50 ms | Cold p95 ms | Warm p95 ms | Visible elements |
|---|---:|---:|---:|---:|
| Explorer, 500 dedicated files, Details, baseline walker | 227.085 | 247.270 | 0.571 | 212 |
| Same Explorer, batched direct-control children | 221.815 | 227.661 | 0.646 | 212 |
| YouTube search results, batched | 155.602 | 168.502 | 0.598 | 136 |
| Notepad Save As, batched | 177.519 | 200.442 | 0.332 | 90 |

Batching is used only when every immediate raw child is a control and the child count fits the remaining visit budget. Otherwise the logical ControlView walker remains, preserving controls behind non-control intermediates. Indexed/visited/depth caps, clipping, ownership, and sibling-cycle checks remain. One immediate-child array is requested from the provider, so its own fetch cost still depends on provider behavior and transaction limits. Explorer's element and omission counts matched before/after; Save As still exposed File name, Save, Cancel, and Items View.

Cold performance still exceeds 100 ms. This is a modest improvement, not performance acceptance. A first Chrome diagnostic failed during UIA initialization with `COMException (0x80004005)` before any samples; its separate complete rerun passed and supplies the table. Artifacts: `review-f4-explorer-500-baseline.json`, `review-f4-explorer-500-batched.json`, `review-f4-youtube-batched-complete.json`, and `review-f4-save-as-batched.json` in `artifacts/benchmarks/`. The failed `review-f4-youtube-batched.json` is excluded.

## F5: event-unavailable fallback

The read-only diagnostic uses 200 back-to-back calls and 200 calls after nominal 35 ms delays, excluding those delays from call timing. UIA event registration is disabled only through an internal diagnostic constructor. No input is sent.

| Events unavailable | Delayed-call p50 ms | p95 ms | p99 ms |
|---|---:|---:|---:|
| Original: full UIA scan after each fallback expiry | 20.770 | 33.612 | 41.141 |
| Full UIA scan limited to once per second | 0.772 | 3.344 | 24.786 |

The after-run recorded nine full UIA scans among 200 delayed calls and no stable-foreground omissions. First discovery, explicit fresh requests, and healthy window-event invalidation remain immediate. Native inventory, foreground-root discovery, and known-handle validation retain their existing fallback refresh cadence. Deterministic scheduling cases cover the 999/1000 ms boundary and override rules.

The measured p95 passes 5 ms for this cadence; p99 still includes scheduled scans, and these results do not imply a hard 5 ms bound on every discovery. Raw reports are `artifacts/benchmarks/review-f5-events-disabled-baseline.json` and `review-f5-events-disabled-throttled.json`. Their diagnostic fields use snake_case; absent `failure` means null was omitted by CoreJson.

## Remaining final checks

The final build and [uninterrupted acceptance](../status/WS1_Final_Review_Acceptance_2026-11-07.md) are complete: 470 automated passes in each configuration, zero failures, five opt-in skips, and all ten owner tasks plus both deletion refusals verified through one Release server PID. The live fixture also passed separately.

`artifacts/benchmarks/review-final-guarded-input-100.json` has 200 measured samples per operation, no failure, correct channels/statuses, and exact text/counters. Final p95: click checks 12.335 ms; click native dispatch 31.230 ms; key checks 9.550 ms; 100-character native Edit checks 14.229 ms; typing native dispatch 60.407 ms; typing total 280.759 ms; verification 212.919 ms. The prior accepted profile measured typing checks 120.307 ms, total 323.146 ms, and verification 147.302 ms. The shortcut substantially reduced checks, but some time moved into verification, and the 10/20 ms budgets remain unmet. Initial full focus p95 was 1.992 ms; six later native checks totaled 0.383 ms p95. Diagnostic scopes are inclusive and must not be added to their enclosing phases.

Final Release Desktop SHA-256 is `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`; harness SHA-256 is `89577F39212483B037562A9A959FC9FB0BFE70DB607BBFFB1419830B9E8B85BC`. These match final acceptance metadata. Earlier observational/pointer reports used the preceding build and retain their own hashes; mouse code, tree batching, and F5 cadence were unchanged by the final RichEdit guard correction.

The paired RDP pointer runs both completed with 200 measured normal/split samples per pooled hook condition and verified counters. In `review-f2-editor-focus-rdp.json`, atomic clicks p95 were 28.800 ms hooks on / 28.286 ms off. In `review-f2-button-focus-rdp.json`, they were 26.507 ms on / 31.436 ms off. Slow tails persist from a non-text button, so text-input context alone is insufficient to explain them. These sequential runs do not establish the exact Windows mechanism. No mouse-dispatch code or budget was changed.

The physical-console comparison and any D2 decision remain pending. Cold observations and Notepad latency remain over budget. Supplementary Unicode/newline physical typing in the installed RichEdit provider is explicitly unsupported; generic Unicode acceptance was verified in Chrome. This is not full Notepad Unicode or performance acceptance.
