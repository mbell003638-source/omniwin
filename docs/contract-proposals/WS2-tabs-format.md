# Proposal: WS2-tabs-format (Q11)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Header with untrusted disclaimer, one line per tab with monotonic `tN` id, active marker, title and URL quoted safely.

## Problem
Contract §9 defines the format for desktop window observations, browser tab observations, and diffs, but lacks a canonical text format specification for `browser_tabs`. Since tab titles and URLs originate from web content, they are untrusted data.

## Proposed Change
1. Always prepend `ResultFormatter.UntrustedHeader` before the tab list.
2. Format each tab as:
   `tab t1 "Page Title" url=https://example.com/path [active]`
   Where `[active]` is appended only if the tab is currently active.
3. Tab titles and URLs must be safely escaped using `ResultFormatter.Quote` rules to prevent newline injection or command injection.

## Impact on Other Streams
- WS0 (ResultFormatter): Add or document `FormatTabs(IReadOnlyList<TabInfo>)`.
- WS2 (Browser): Implemented in `BrowserTools` and `BrowserEngine`.
