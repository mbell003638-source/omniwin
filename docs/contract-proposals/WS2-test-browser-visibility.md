# Proposal: WS2-test-browser-visibility (Q14)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Run tests with `--headless=new` or acquire `Local\OmniWin.UiInputTestLock` when launching visible browser windows.

## Problem
In orchestration contract 03 §4, test runs that open visible UI windows can steal OS focus and disrupt concurrent desktop tests or owner workstation activity. CDP allows running browsers in `--headless=new` mode for automated testing without displaying a window.

## Proposed Change
1. Unit and automated fixture tests in `OmniWin.Browser.Tests` launch the browser with `--headless=new` by default, eliminating window popup and foreground interference.
2. If an interactive or visible browser test is requested (e.g. via an environment flag `OMNIWIN_BROWSER_HEADED=1`), the test must acquire `Local\OmniWin.UiInputTestLock` for the duration of the test run.

## Impact on Other Streams
- WS1 / WS4: Zero disruption to desktop UI tests running concurrently.
- WS2: Configured in `BrowserLauncher` and test fixture setup.
