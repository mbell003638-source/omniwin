# Proposal: WS2-fixture-pages (Q15)

## Status
Approved by the owner 2026-11-07 ("yes to all"). WS2 builds the static fixture pages in `testapps/web-fixtures/` and a local `HttpListener` fixture server in `OmniWin.Browser.Tests` for M1 testing.

## Problem
In the original parallel brief, WS4 was slated to build the web fixture pages in `testapps/web-fixtures/`. As noted in the reply to start WS2, `testapps/web-fixtures/` is currently empty and WS2 is authorized to build the fixture pages and tests directly.

## Proposed Change
1. Populate `testapps/web-fixtures/` with the standard fixture suite:
   - `index.html`: Navigation hub
   - `controls.html`: Interactive controls with `#event-log` event recorder
   - `delete.html`: Delete button, link, and form for testing `policy_forbidden`
   - `dialogs.html`: In-page modals and overlays for occlusion testing
   - `large.html`: >150 elements for observe truncation and limit testing
   - `dynamic.html`: Mutating elements for generation diff testing
2. In `OmniWin.Browser.Tests`, provide an embedded `LocalFixtureServer` hosting these files over `http://127.0.0.1:<port>/`.

## Impact on Other Streams
- WS4: Can reuse these exact fixture pages for integration test suite.
- WS2: Self-contained test execution against local HTTP server.
