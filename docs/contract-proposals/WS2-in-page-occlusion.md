# Proposal: WS2-in-page-occlusion (Q10)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Evaluate in-page hit-testing via `document.elementFromPoint`; occluded targets return `blocked occluded_same_window` or `blocked modal_blocking`.

## Problem
In contract §10.2, reason codes include `occluded_by_window`, `occluded_same_window`, and `modal_blocking`. For web pages, elements may be physically hidden under fixed position overlays, dropdown menus, floating cookie consent banners, or backdrop dialog divs.

## Proposed Change
1. During pre-check for pointer actions (`click`, `double_click`, `hover`), the engine computes the center of the element's client rect and performs `document.elementFromPoint(x, y)`.
2. If the hit node is not the element or a descendant/ancestor container, check whether the covering element belongs to a modal backdrop (`dialog`, `role="dialog"`, or `aria-modal="true"`).
3. If covered by a modal element, return `blocked modal_blocking`. Otherwise return `blocked occluded_same_window`.

## Impact on Other Streams
- WS0: Uses standard reason codes already present in `ReasonCodes.cs`.
- WS2: Implemented in `PageScript.js` and `BrowserEngine`.
