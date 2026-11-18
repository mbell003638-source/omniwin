# Proposal: WS2-wiring-and-audit (Q3)

## Status
Approved by the owner 2026-11-07 ("yes to all"). DI container injects safety services; tools write audit records.

## Problem
In contract §5 and §13.4, the browser engine is specified to start lazily on first browser tool call, and every browser tool call must be audited via `IAuditLog`. The contract needs to clarify whether the server host/DI container creates the engine with dependencies (`ISafetyGate`, `IKillSwitch`, `IUserActivity`, `IMetrics`, `IAuditLog`), and whether the MCP tool handler in `BrowserTools` records the audit entry or the engine itself does.

## Proposed Change
1. The DI container in `OmniWin.Server` registers `IBrowserEngine` as a singleton initialized lazily on first request, taking `ISafetyGate`, `IKillSwitch`, `IUserActivity`, `IMetrics`, `OmniWinConfig`, and `OmniWinPaths`.
2. The MCP tool handlers in `BrowserTools.cs` write the `IAuditLog` entry for each tool invocation (matching desktop tool pattern), logging redacted arguments, status, input_sent, duration, and reason code.
3. The engine records internal phase metrics via `IMetrics`.

## Impact on Other Streams
- WS0 (Server / Hosting): Server registers `IBrowserEngine` in DI and passes dependencies.
- WS2 (Browser): Implemented in `BrowserEngine` and `BrowserTools`.
