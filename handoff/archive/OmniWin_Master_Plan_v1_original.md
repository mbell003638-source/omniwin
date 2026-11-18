# 🛸 Project OmniWin: Unified Windows Computer-Use Engine
## Master Architecture, Technical Synthesis, and Frontier AI Handoff Plan

---

## 1. Executive Summary & Project Goal

### The Problem with Current "Computer Use" Agents
Traditional computer-use agents (e.g., Anthropic Claude Computer Use, OpenAI Operator) rely on a purely visual feedback loop:
$$\text{Full-Screen Screenshot (2MB PNG)} \longrightarrow \text{Vision LLM} \longrightarrow \text{Predicted } (x, y) \text{ Pixels} \longrightarrow \text{Blind Click}$$

This architecture suffers from critical real-world failure modes:
1. **Severe Latency:** 3 to 8 seconds per individual action.
2. **High Token Cost:** 1,500 to 2,500 vision tokens per step ($0.03–$0.10/step).
3. **Fragility & Hallucination:** Models frequently misclick buttons by a few pixels, choke on Retina/DPI scaling, or hallucinate elements in visually busy user interfaces.
4. **No Verification:** Keystrokes are blindly injected without verifying whether the active editor has keyboard focus, often spilling private text into the wrong application.

### The Objective
To design and build **Project OmniWin**: an open, production-grade, sub-second latency, zero-hallucination Windows Automation Engine exposed via the **Model Context Protocol (MCP)** to Antigravity, Claude Code, or any cloud/local LLM.

Instead of taking screenshots, OmniWin merges the best ideas from two state-of-the-art open-source agents:
- **Computah** (GitHub: [https://github.com/musubipapi/computah](https://github.com/musubipapi/computah)): Native Accessibility Tree inspection, pre-flight hit-testing verification before physical clicks, focus-locking before typing, and before/after state diff verification.
- **Macbrow** (GitHub: [https://github.com/timpratim/macbrow](https://github.com/timpratim/macbrow)): Fast tri-modal routing (~300ms), Chrome CDP web driving at the DOM level without screenshot overhead, headless scripting for instant system toggles, and self-learning tool creation with a security policy sandbox.

Adapted natively to **Microsoft Windows 11**.

---

## 2. Technical Knowledge Synthesis: What We Know

### A. Core Insights from Computah (https://github.com/musubipapi/computah)
1. **Accessibility Hierarchy Over Pixels:** Interrogates the native OS UI tree directly. Sub-300ms reads, resolution-independent, and immune to Retina/DPI shifts.
2. **Pre-Flight Hit Testing (`checkedClick`):** Before clicking $(x, y)$, queries the window server to verify that the element under the cursor is genuinely the target control and not an obscuring modal or notification banner.
3. **Physical Click vs. Programmatic Action:** Prefers physical clicks (`SendInput`). If an overlay or custom rendering layer blocks hit-testing, falls back to programmatic accessibility patterns (`InvokePattern`), but **never fires both**.
4. **Focus-Locked Typing:** Verifies that keyboard focus is active on the target editor before typing; breaks text into UTF-16 chunks, validating that the target PID and focused node do not change mid-input.
5. **"Never Repeat Uncertain Input":** If an action's post-state cannot be verified through UI tree diffing, it halts rather than blindly retrying, preventing destructive double submissions.
6. **Constrained Decision Modeling:** Uses a multiple-choice schema where the model selects from real observed controls (`n7_typeText`, `n9_pressClick`, `none_here`). The model cannot hallucinate nonexistent buttons.
7. **Computah's Limitation:** Fails on massive web pages (e.g., 30,000 DOM nodes in complex web applications) because desktop accessibility traversals time out.

### B. Core Insights from Macbrow (https://github.com/timpratim/macbrow)
1. **Fast Tri-Modal Intent Routing:** Routes commands in ~300ms into `browser_task`, `tool_script`, or `desktop_action`.
2. **Browser Automation via CDP (`jev-ultrafast`):** Bypasses desktop accessibility lag by connecting directly to the Chrome DevTools Protocol (CDP). Pages are read as compact tables of interactive DOM elements. Actions are executed in milliseconds.
3. **Headless Scripting:** Uses native system scripts for instant system actions (volume, audio, window snapping, network status) in 10ms without touching the mouse cursor.
4. **Self-Generating Tool Registry:** When an action lacks a pre-existing script, an LLM drafts a new tool, verifies it against a strict safety sandbox (`policy.py`), tests it, and caches it in `tools/registry.json`.
5. **Macbrow's Limitation:** Blind to native desktop apps that lack scripting dictionaries, and limited only to Chrome.

---

## 3. The Unified Architecture on Microsoft Windows

By combining both paradigms on Windows, each engine completes the limitations of the other:

```
                          ┌────────────────────────────────┐
                          │   Cloud / Antigravity Agent    │
                          └───────────────┬────────────────┘
                                          │ (MCP Protocol / stdio / SSE)
                          ┌───────────────▼────────────────┐
                          │   Windows Omni-Control Server  │
                          └───────┬───────────────┬────────┘
                                  │               │
        ┌─────────────────────────┼───────────────┴─────────────────────────┐
        ▼                         ▼                                         ▼
┌──────────────────┐    ┌──────────────────┐                     ┌─────────────────────┐
│ 1. Native UI Engine│  │ 2. Web Engine    │                     │ 3. Script Engine    │
│ (Computah Style) │    │ (Macbrow Style)  │                     │ (Macbrow Style)     │
├──────────────────┤    ├──────────────────┤                     ├─────────────────────┤
│ Windows UI       │    │ Chrome & Edge    │                     │ PowerShell Core /   │
│ Automation (UIA) │    │ CDP (Playwright) │                     │ Win32 COM / WMI     │
│ • Hit-test checks│    │ • Compact DOM    │                     │ • Fast system calls │
│ • Focus locking  │    │ • Cookie auth    │                     │ • Safety policy     │
│ • Win32 SendInput│    │ • Tab management │                     │ • Tool learning     │
└──────────────────┘    └──────────────────┘                     └─────────────────────┘
```

### Windows Native Implementation Mapping

| Subsystem | macOS Implementation | Windows Adaptation | Advantages of Windows |
|---|---|---|---|
| **Native UI Tree** | `AXUIElement` / `ApplicationServices` | **Windows UI Automation (`IUIAutomation`)** | Standardized across Win32, WPF, WinUI 3, Electron, and UWP. Clean COM API accessible via Python `uiautomation` / `comtypes`. |
| **Input Injection** | `CGEvent` (Quartz Events) | **Win32 `SendInput` API** | Extremely reliable hardware-level event synthesis with scan codes and virtual keys. |
| **Pre-Flight Hit Testing** | `AXUIElementCopyElementAtPosition` | **Win32 `WindowFromPoint` & `IUIAutomation.ElementFromPoint`** | Confirms the exact native window handle and accessibility node under $(x, y)$. |
| **Headless Scripting** | AppleScript (`osascript`) | **PowerShell Core 7 (`pwsh.exe`)** | Returns structured .NET objects and JSON instead of fragile text. Far superior to AppleScript. |
| **Web Browser Automation** | Chrome CDP | **Chrome / Microsoft Edge CDP** | Edge is pre-installed on every Windows PC with native CDP debugging support. |
| **Safety Sandbox** | `policy.py` (Macbrow) | **`policy.py` (Windows AST / Regex / Taint Checker)** | Blocks destructive operations (`Format-Volume`, recursive deletes, registry tampering, credential access). |

---

## 4. MCP Server Interface & Tool Definitions

The Windows Omni-Control Server is packaged as a standard Python MCP server exposing 5 deterministic tools:

1. `desktop_inspect(process_name=None, max_nodes=500)`:
   - Traverses the active window via UI Automation.
   - Returns a structured list of visible, actionable controls with assigned IDs (`n1_button`, `n4_input`). Redacts secure fields and `.env` files.
2. `desktop_act(node_id, action, text=None)`:
   - Executes pre-flight hit-testing, physical click via `SendInput`, or focus-locked typing.
   - Takes a post-action snapshot to verify outcome (`complete`, `progress`, `contradicted`, `unknown`).
3. `browser_act(action, target_index, value=None)`:
   - Interacts with interactive web elements directly via Chrome/Edge CDP.
4. `powershell_run(script)`:
   - Executes fast headless PowerShell scripts screened through the safety policy sandbox.
5. `tool_learn(name, description, script)`:
   - Validates, tests, and caches a newly created script into `tools/registry.json` for persistent future reuse.

---

## 5. Phased Implementation Roadmap

- **Phase 1: Native Windows Desktop Engine (`desktop_engine.py`)**
  - Implement `IUIAutomation` tree traversal (max depth 25, node limit 500, timeout 0.4s).
  - Implement Win32 `SendInput` dispatcher with `WindowFromPoint` hit-testing.
  - Implement focus-locked text typing with chunking and active window verification.
  - Implement snapshot diffing for state verification.

- **Phase 2: Browser CDP Engine (`browser_engine.py`)**
  - Connect to Chrome / Microsoft Edge via remote debugging port (`9222`).
  - Extract interactive DOM elements with flat numerical indices.
  - Dispatch clicks, text input, and tab navigation.

- **Phase 3: Headless PowerShell & Safety Policy (`script_engine.py` & `policy.py`)**
  - Implement safe runner for `pwsh.exe` with execution timeouts and output parsing.
  - Implement safety policy: whitelist allowed system categories (volume, display, media, apps) and block destructive operations.

- **Phase 4: Tool Generator & Registry (`registry.py`)**
  - Dynamic loading and caching of reusable PowerShell automation scripts in JSON format.

- **Phase 5: MCP Server Integration (`server.py`)**
  - Wrap all components in the official `mcp` Python SDK over `stdio`.

---

## 6. The Master Frontier AI Prompt

> **Instructions:** Copy the entire block below and paste it into a top-tier frontier model (e.g., Claude 3.7 Sonnet, Claude 3.5 Sonnet, GPT-4.5/GPT-5, or specialized coding model) to generate the complete production codebase.

```markdown
# TASK: Architect and Scaffold "OmniWin" — A Production Windows Computer-Use MCP Server

You are a Principal Systems Software Engineer and AI Agent Architect specializing in Windows internals, UI Automation, the Model Context Protocol (MCP), and agentic workflows.

### Objective
We are building a production-grade Windows computer-use system that eliminates slow, expensive, and fragile screenshot/vision models (such as Anthropic Computer Use). Instead, we are fusing the architectures of two state-of-the-art open-source agents:
1. **Computah** (https://github.com/musubipapi/computah): Native accessibility tree reading (AXUIElement), pre-flight hit-testing verification before clicking, focus-locking before typing, and before/after state diff verification.
2. **Macbrow** (https://github.com/timpratim/macbrow): Tri-modal fast routing (~300ms), Chrome CDP web driving (DOM-level, no screenshots), headless scripting for instant system toggles, and a self-learning tool generator protected by a strict security policy.

We want this adapted natively to **Microsoft Windows 11** and exposed as a clean **Python MCP Server** that any client (Antigravity, Claude Code, OpenAI) can invoke.

---

### Core Requirements

1. **Native Desktop UI Engine (`desktop_engine.py`)**:
   - Use Windows UI Automation (`IUIAutomation` via Python `uiautomation` or `comtypes`).
   - Extract active window UI tree into clean, compact JSON with assigned node IDs (e.g., `n1_button`, `n4_input`). Redact secure fields and `.env` editors.
   - **Pre-flight Hit-Testing:** Before dispatching a mouse click via Win32 `SendInput`, call `WindowFromPoint` / `IUIAutomation.ElementFromPoint` to verify the pixel is owned by the target element and not obscured by an overlapping overlay or dialog.
   - **Physical Click vs. InvokePattern:** Prefer physical click; fallback to programmatic `InvokePattern` only if hit-testing is blocked; never fire both.
   - **Focus-Locked Typing:** Verify keyboard focus on the control before emitting keystrokes; type in UTF-16 chunks with active window validation.
   - **Post-State Verification:** Compare snapshots before and after input to confirm changes (window title, control value, dialog appearance). If unconfirmed, halt ("Never repeat uncertain input").

2. **Web Engine (`browser_engine.py`)**:
   - Drive Chrome or Microsoft Edge via Chrome DevTools Protocol (CDP) / Playwright over a remote debugging port.
   - Extract interactive DOM elements with flat numerical indices.
   - Dispatch click, type, and scroll directly via CDP without taking screenshots.

3. **Headless Script Engine (`script_engine.py`)**:
   - Execute fast PowerShell Core (`pwsh.exe`) commands for system tasks (volume, window management, app launching, network queries).
   - Enforce a strict **Safety Policy Sandbox (`policy.py`)**: block disk formatting, recursive deletions, arbitrary credential scraping, registry deletions, and terminal tampering.
   - Include a **Learned Tool Registry (`registry.py`)**: allow the agent to draft new PowerShell tools, test them, and cache them in `tools/registry.json`.

4. **MCP Server Interface (`server.py`)**:
   - Expose the system using the standard `mcp` Python SDK with the following tools:
     - `desktop_inspect(process_name: Optional[str] = None)`
     - `desktop_act(node_id: str, action: str, text: Optional[str] = None)`
     - `browser_act(action: str, target_index: int, value: Optional[str] = None)`
     - `powershell_run(script: str)`
     - `tool_learn(name: str, description: str, script: str)`

---

### Deliverables Expected from You

1. **System Design & Class Diagram**: Architectural layout showing how requests flow through the MCP server to the three engines.
2. **Production-Ready Python Code Scaffolds**:
   - `desktop_engine.py`: Complete UI Automation + Win32 `SendInput` + Hit-test validation.
   - `browser_engine.py`: CDP connection and DOM manipulation.
   - `script_engine.py` & `policy.py`: Safe PowerShell execution with policy gates.
   - `server.py`: Complete MCP server registering all tools and handling state.
3. **Setup and Dependency Guide**: Instructions for `pyproject.toml` / `requirements.txt` and Windows prerequisites.

Make the code modular, type-annotated, asynchronous, robust against crashes, and ready for production deployment.
```

---

## 7. Handoff & Build Protocol

When the external AI finishes generating the architectural code and specifications:
1. Copy the code files or response directly back into your Antigravity conversation.
2. Antigravity will execute the following steps automatically:
   - Create the workspace directory at `C:\Users\<you>\.gemini\antigravity\scratch\windows-omni-mcp`.
   - Write out all modular files (`desktop_engine.py`, `browser_engine.py`, `script_engine.py`, `policy.py`, `registry.py`, `server.py`).
   - Set up the Python virtual environment and install prerequisites (`pip install mcp uiautomation playwright pywin32`).
   - Register the server in your local Antigravity MCP configuration.
   - Run verification diagnostics to confirm that native UI Automation, browser CDP, and safe PowerShell execution work seamlessly on your Windows system.
