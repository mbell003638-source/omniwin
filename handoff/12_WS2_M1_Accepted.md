# WS2 M1: accepted

Copy everything below the line and send it to the AI.

---

The reviewer checked your P0 report against the code, a fresh test run and the raw files.

**Verdict: Yes, WS2 M1 is accepted.** Don't start M2 until the owner says so.

## What the reviewer confirmed

- **P0:**
  - `BlockNonWebSchemes` and both refusals are gone, and `BrowserEngine.cs:39-40` records the owner decision.
  - The label functions stay and only record.
  - `nonweb-links.html` is gone. The only links on `controls.html` are the `data:` link and the http link, and no fixture page links to a risky scheme.
  - The remaining risky strings in the tests are pure string checks. The `ClassifyNavRisk` theory at `:403-416` and the guard cases at `:856-859` open nothing.
  - The contract's §13.1 bullet is correct.
- **P1, P2 and the low items:** unchanged and sound. Post-input failures return results, and `NavigationGuard` is installed in every browser test and in the benchmark.
- **Builds and tests:** the reviewer ran Release, then Debug, with `-warnaserror`: 0 warnings, 0 errors. Both test runs had 570 total, 564 passed, 6 skipped, 0 failed. The drop from 573 matches your explanation: 11 test cases removed, 8 turned into the pure `ClassifyNavRisk` theory.
- **Clean-up:** 20 s after the runs there was no Edge, test-host or Settings process left, and nothing new in Downloads.
- **Hashes:** Browser `70D005D9…F31E`, Core `254441C2…FC64`, Safety `4FF11EBE…E6C` and Desktop `C773884D…0C0B` match your report. No file in Core, Safety, Desktop or Server changed after round 1's contract-version edit.
- **The build lock, PID 30796:** that was most likely the reviewer's own test run. It ran while you were editing. Nothing for you to fix.

## Small follow-ups (next time you touch the tests, not now)

- **`NavigationGuard_throws_before_a_non_local_navigate_is_sent`** (`BrowserEngineTests.cs:878`) sends `Target.createTarget` with an `ms-settings:` URL. That only matters if the guard itself breaks, but then it could open Settings. Use a harmless non-local URL instead, such as `http://omniwin-guard-test.invalid/`, which can never resolve.
- **The guard after a reconnect:** it's set once, after `EnsureConnectedAsync`. If the engine ever reconnects inside a test, the new connection has no guard. Either install it from a hook the engine calls on every new connection, or assert in each test's teardown that it's still installed.

## Still open (owner decisions; don't act on them)

- **Q6 Risky label:** the owner changed the decision on 2026-11-08 and approved wiring it in now. The instructions are in `13_WS2_Q6_Risk_Label_Task.md`, which replaces any earlier "not now".
- **Not implemented, for M2:**
  - Q7: `window_opened` and `window_closed` on tabs;
  - Q9: frame counting;
  - Q3: audit records from the tools;
  - Q8: dialog interception;
  - Q11: the rich tabs list.
- **M2 note:** `ResolveSelectorAsync` needs to record its scan and return a generation. Write that up as a proposal when M2 starts.
- **Speed:** the owner accepted slower speed on bigger pages. Contract §15 still says 10 ms. Rewording it waits on the owner.

## Reply

There's nothing to change now. Acknowledge this note, and wait for the owner before starting M2.
