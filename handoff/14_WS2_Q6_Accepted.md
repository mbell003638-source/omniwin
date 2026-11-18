# WS2 Q6 Risky label: accepted

Copy everything below the line and send it to the AI.

---

The reviewer checked your Q6 report against the code, a fresh test run and the raw files.

**Verdict: Yes, the Risky label is accepted.** Don't start M2 until the owner says so.

## What the reviewer confirmed

- **Scope:** the files changed this round are exactly the ones you listed. No Safety or Desktop source changed. The gate and the delete policy are untouched.
- **Label only:**
  - `Risk` is set only at the end of `NavigateAsync` (`BrowserEngine.cs:359`) and `ActAsync` (`:611`).
  - The classification at `:672` is stored and never read by any check, so it can't block, delay or change an action.
- **Format:**
  - `risk=risky` is printed only for Risky results (`ResultFormatter.cs:100-103`).
  - The first line holds only engine-made tokens, with no page text, so a page can't fake or hide the label.
  - `CoreJson` drops null fields, so existing audit lines and every desktop result are unchanged.
- **Audit:** `ToolCallAudit.BuildEntry` records `risky` only for that exact token.
- **Builds and tests:** the reviewer ran Release, then Debug, with `-warnaserror`: 0 warnings, 0 errors. Both test runs had 589 total, 583 passed, 6 skipped, 0 failed.
- **Clean-up:** 20 s later there was no Edge, test-host or Settings process left, and nothing new in Downloads.
- **Hashes:** all five match your report:
  - Browser `D09496E3…A658`
  - Core `C2BFF80F…7248`
  - Safety `9AF2E85A…953F`
  - Desktop `6A05DD17…050D`
  - Server `4360F233…7BBC`
- **The two extra project-file edits** (Server `InternalsVisibleTo`, and the Integration project's Server reference) are accepted. They only let the test reach internal code, and you reported them.

## Small follow-ups (do them now; they're doc-only)

- **`WS2.md:62`** still says the risk-label proposal is "awaiting owner decision". Change it to implemented.
- **`02_Architecture_and_Contracts.md`:** add the new optional `Risk` parameter to the `ActResult` and `AuditEntry` C# samples, so the samples match the code. Change nothing else in the file.

## Notes for M2 (don't change now)

The audit only helps if it shows what the browser did. When the browser tools are registered in M2, review `ToolCallAudit.Redact`:

- **Refs:** browser refs (`bN`) and tab ids (`tN`) aren't in its safe-ref list, which only allows ones starting with `w`, `e` or `o`. They would be logged as `<3 chars>`.
- **URLs:** a `url` argument isn't logged at all.

Propose what to log, such as the ref, tab id, and the URL's scheme and host. Then a Risky line also shows which link or address it was.

Also still open:
- Q7, Q9, Q3, Q8 and Q11, as before;
- the `ResolveSelectorAsync` proposal;
- the two test follow-ups from `12_WS2_M1_Accepted.md`: the guard test's `ms-settings:` URL, and the guard after a reconnect.

## Reply

Make the two doc follow-ups, say which lines you changed, then stop.
