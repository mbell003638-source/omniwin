# Proposal: WS2-frames (Q9)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Cross-origin frames are listed in the frame count and header; if script injection cannot access them due to origin boundaries, elements inside them return `blocked cross_origin_frame`.

## Problem
In contract §9.1 and §10.2, iframe elements have ref prefixes such as `b23 (f2) link "Docs"`, and reason code `cross_origin_frame` exists. Cross-origin frames run in isolated origin sandboxes (and sometimes separate processes via Site Isolation).

## Proposed Change
1. All frame IDs (`f1`, `f2`, ...) are tracked monotonically per session.
2. If an iframe is cross-origin and accessible via CDP OOPiF (out-of-process iframe) target attachment, the injected index operates in each frame context.
3. If a cross-origin iframe cannot be injected or accessed, the iframe container element itself is presented with state `cross_origin` and attempts to act on un-indexable internal elements fail with reason `cross_origin_frame`.

## Impact on Other Streams
- WS0: Documented reason code `cross_origin_frame`.
- WS2: Frame indexer and session multiplexer in `OmniWin.Browser`.
