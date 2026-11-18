namespace OmniWin.Browser;

/// <summary>
/// Injected script maintaining the in-page DOM index, element refs, occlusion checks,
/// password/payment redaction, and diff computation.
/// </summary>
public static class PageScript
{
    public const string InjectedScript = @"
(function() {
  if (window.__omniwin) return;

  const registry = new Map(); // bN -> Element
  let siblingIndexCache = new WeakMap(); // parent -> Map(child -> index among siblings with the same tag), rebuilt per scan
  const MAX_CONSIDERED_ELEMENTS = 3000; // candidates inspected per scan; the rest are counted, not read
  const nodeToRef = new WeakMap(); // Element -> bN
  let currentGen = 1;
  let elementMap = new Map(); // bN -> element info object

  function isPaymentField(el) {
    const ac = (el.getAttribute('autocomplete') || '').toLowerCase();
    if (ac.startsWith('cc-')) return true;
    const name = (el.name || el.id || '').toLowerCase();
    return name.includes('cardnumber') || name.includes('cvv') || name.includes('cvc') || name.includes('creditcard');
  }

  // True only when this exact element is the document's active element (looking through open shadow roots).
  function isActuallyFocused(el) {
    let active = document.activeElement;
    while (active && active.shadowRoot && active.shadowRoot.activeElement) {
      active = active.shadowRoot.activeElement;
    }
    return active === el;
  }

  function getElementRole(el) {
    const explicit = el.getAttribute('role');
    if (explicit) return explicit.toLowerCase();
    const tag = el.tagName.toLowerCase();
    if (tag === 'button') return 'button';
    if (tag === 'a' && el.hasAttribute('href')) return 'link';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return 'combobox';
    if (tag === 'input') {
      const type = (el.type || 'text').toLowerCase();
      if (type === 'button' || type === 'submit' || type === 'reset') return 'button';
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      return 'textbox';
    }
    if (tag === 'dialog') return 'dialog';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    return tag;
  }

  function getElementName(el) {
    const ariaLabel = el.getAttribute('aria-label');
    if (ariaLabel) return ariaLabel.trim();

    const ariaLabelledBy = el.getAttribute('aria-labelledby');
    if (ariaLabelledBy) {
      const targets = ariaLabelledBy.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean);
      if (targets.length > 0) {
        return targets.map(t => t.innerText || t.textContent || '').join(' ').trim();
      }
    }

    if (el.labels && el.labels.length > 0) {
      return el.labels[0].innerText.trim();
    }

    const tag = el.tagName.toLowerCase();
    if (tag === 'input' || tag === 'button') {
      const val = el.value || el.innerText || el.textContent;
      if (val && tag === 'button') return val.trim();
    }

    if (el.innerText && el.innerText.trim()) {
      return el.innerText.trim();
    }

    if (el.title) return el.title.trim();
    if (el.placeholder) return el.placeholder.trim();
    if (el.name) return el.name.trim();
    if (el.id) return el.id.trim();

    return '';
  }

  function getActionsForElement(role, tag) {
    const actions = [];
    if (role === 'button' || role === 'link' || tag === 'a' || tag === 'button') {
      actions.push('click');
    }
    if (role === 'textbox' || tag === 'input' || tag === 'textarea') {
      actions.push('type', 'set_value', 'clear');
    }
    if (role === 'checkbox') {
      actions.push('click', 'check', 'uncheck');
    }
    if (role === 'radio') {
      actions.push('click', 'check');
    }
    if (role === 'combobox' || tag === 'select') {
      actions.push('select');
    }
    if (actions.length === 0) {
      actions.push('click');
    }
    return actions;
  }

  // Name of a container that is cheap to compute: never its whole text (a list with thousands of rows would make every
  // element on the page pay for reading all of it).
  function getCheapName(el) {
    const ariaLabel = el.getAttribute('aria-label');
    if (ariaLabel) return ariaLabel.trim();
    if (el.title) return el.title.trim();
    if (el.id) return el.id.trim();
    return '';
  }

  // Index of el among its siblings with the same tag, computed once per parent per scan.
  function siblingIndex(el) {
    const parent = el.parentElement;
    if (!parent) return 0;
    let map = siblingIndexCache.get(parent);
    if (!map) {
      map = new Map();
      const counters = {};
      for (const child of parent.children) {
        const n = counters[child.tagName] || 0;
        map.set(child, n);
        counters[child.tagName] = n + 1;
      }
      siblingIndexCache.set(parent, map);
    }
    return map.has(el) ? map.get(el) : 0;
  }

  function buildSelectorPath(el) {
    const path = [];
    let curr = el;
    while (curr && curr !== document.body && curr !== document.documentElement) {
      const role = getElementRole(curr);
      const autoId = curr.id || curr.getAttribute('data-testid') || null;
      // A container with many children uses its cheap name; ordinary pages keep the full name exactly as before.
      const name = (curr !== el && curr.childElementCount > 50 ? getCheapName(curr) : getElementName(curr)) || null;
      const className = curr.className && typeof curr.className === 'string' ? curr.className : null;
      const index = siblingIndex(curr);
      path.unshift({ role, automationId: autoId, name, className, index });
      curr = curr.parentElement;
    }
    return path;
  }

  window.__omniwin = {
    registry,
    currentGen,
    elementMap,

    scan: function(startRefId, maxElements, includeText, viewportOnly, stride) {
      const step = stride || 1;
      siblingIndexCache = new WeakMap();
      const candidates = document.querySelectorAll(
        'button, a[href], input, textarea, select, [role], [tabindex], [contenteditable], dialog'
      );

      let nextId = startRefId || 1;
      let omittedOffscreen = 0;
      let omittedNonInteractive = 0;
      let cappedAtLimit = 0;
      let considered = 0;
      const visibleElements = [];

      for (const el of candidates) {
        // Hard cap on the work done per scan (a 10,000-button page took longer than the tool timeout). Candidates past the
        // cap are not inspected; they are counted and reported as capped so the omission is never silent.
        if (++considered > MAX_CONSIDERED_ELEMENTS) {
          cappedAtLimit = candidates.length - MAX_CONSIDERED_ELEMENTS;
          break;
        }

        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') {
          omittedNonInteractive++;
          continue;
        }

        const rect = el.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) {
          omittedNonInteractive++;
          continue;
        }

        const isOffscreen = rect.bottom < 0 || rect.top > window.innerHeight || rect.right < 0 || rect.left > window.innerWidth;
        if (viewportOnly && isOffscreen) {
          omittedOffscreen++;
          continue;
        }

        let ref = nodeToRef.get(el);
        if (!ref) {
          ref = 'b' + nextId;
          nextId += step;
          nodeToRef.set(el, ref);
        }
        registry.set(ref, el);

        const role = getElementRole(el);
        const name = getElementName(el);
        const autoId = el.id || el.getAttribute('data-testid') || null;

        const isPwd = el.type === 'password' || el.getAttribute('type') === 'password';
        const isPay = isPaymentField(el);
        const isRedacted = isPwd || isPay;

        let val = '';
        if (isRedacted) {
          val = '<redacted>';
        } else if (el.value !== undefined) {
          val = String(el.value);
        } else if (el.getAttribute('value')) {
          val = el.getAttribute('value');
        } else if (el.tagName && el.tagName.toLowerCase() === 'a' && el.getAttribute('href')) {
          val = el.getAttribute('href');
        }

        const states = [];
        if (document.activeElement === el) states.push('focused');
        const isDisabled = el.disabled || el.getAttribute('aria-disabled') === 'true';
        if (isDisabled) states.push('disabled');
        if (el.readOnly || el.getAttribute('aria-readonly') === 'true') states.push('readonly');
        if (el.checked || el.getAttribute('aria-checked') === 'true') states.push('checked');
        else if (el.type === 'checkbox' || el.type === 'radio') states.push('unchecked');
        if (el.required || el.getAttribute('aria-required') === 'true') states.push('required');
        if (el.getAttribute('aria-expanded') === 'true') states.push('expanded');
        if (el.getAttribute('aria-expanded') === 'false') states.push('collapsed');

        // Occlusion check
        const cx = rect.left + rect.width / 2;
        const cy = rect.top + rect.height / 2;
        let occlusion = null;

        if (cx >= 0 && cx <= window.innerWidth && cy >= 0 && cy <= window.innerHeight) {
          const hit = document.elementFromPoint(cx, cy);
          if (hit && hit !== el && !el.contains(hit)) {
            let modalAncestor = hit.closest('dialog, [role=""dialog""], [aria-modal=""true""]');
            if (modalAncestor) {
              occlusion = 'modal_blocking';
            } else {
              occlusion = 'occluded_same_window';
            }
          }
        }

        const actions = getActionsForElement(role, el.tagName.toLowerCase());
        const path = buildSelectorPath(el);

        visibleElements.push({
          ref,
          role,
          name,
          value: val,
          isPassword: isRedacted,
          isEnabled: !isDisabled,
          automationId: autoId,
          states,
          actions,
          bounds: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
          center: { x: cx, y: cy },
          occlusion,
          isOffscreen,
          path
        });
      }

      currentGen++;
      const newMap = new Map();
      for (const item of visibleElements) {
        newMap.set(item.ref, item);
      }
      elementMap = newMap;

      return {
        generation: currentGen,
        nextRefId: nextId,
        elements: visibleElements,
        omittedOffscreen,
        omittedNonInteractive,
        cappedAtLimit,
        url: window.location.href,
        title: document.title,
        frames: document.querySelectorAll('iframe,frame').length,
        frameDetails: Array.from(document.querySelectorAll('iframe,frame')).map(function (f) {
          let accessible = false;
          let loading = false;
          try {
            const d = f.contentDocument;
            accessible = !!d;
            // Still loading: not complete yet, or still the empty first document while a real page is on its way.
            const src = f.src || '';
            loading = !!d && (d.readyState !== 'complete' ||
              (src !== '' && src !== 'about:blank' && d.URL === 'about:blank'));
          } catch (e) { accessible = false; }
          return { src: f.src || '', accessible: accessible, loading: loading };
        })
      };
    },

    // Rectangles (viewport coordinates) of every password and payment field, plus every iframe whose document can't
    // be read (cross-origin), so a screenshot can black them out. Used by browser_view only.
    // maskAllFrames is true inside a child frame: a frame nested inside it is deeper than one level, so it is not searched
    // and is blacked out whole (like a cross-origin frame). Open shadow roots are walked; closed ones are found through CDP.
    sensitiveRects: function(maskAllFrames) {
      const rects = [];
      const add = function(el) {
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) rects.push({ x: r.left, y: r.top, w: r.width, h: r.height });
      };
      const walk = function(scope) {
        for (const el of scope.querySelectorAll('input, textarea')) {
          const isPwd = el.type === 'password' || el.getAttribute('type') === 'password';
          if (isPwd || isPaymentField(el)) add(el);
        }
        for (const f of scope.querySelectorAll('iframe,frame')) {
          let ok = false;
          try { ok = !!f.contentDocument; } catch (e) { ok = false; }
          if (!ok || maskAllFrames) add(f);
        }
        for (const host of scope.querySelectorAll('*')) {
          if (host.shadowRoot) walk(host.shadowRoot);
        }
      };
      walk(document);
      return {
        rects: rects,
        scrollX: window.scrollX,
        scrollY: window.scrollY,
        dpr: window.devicePixelRatio,
        title: document.title
      };
    },

    getElement: function(ref) {
      return registry.get(ref) || null;
    },

    inspectElement: function(ref) {
      siblingIndexCache = new WeakMap();
      const el = registry.get(ref);
      if (!el || !el.isConnected) return { found: false };

      const rect = el.getBoundingClientRect();
      const role = getElementRole(el);
      const name = getElementName(el);
      const autoId = el.id || el.getAttribute('data-testid') || null;
      const isPwd = el.type === 'password' || el.getAttribute('type') === 'password';
      const isPay = isPaymentField(el);
      const isRedacted = isPwd || isPay;

      let val = '';
      if (isRedacted) {
        val = '<redacted>';
      } else if (el.value !== undefined) {
        val = String(el.value);
      } else if (el.getAttribute('value')) {
        val = el.getAttribute('value');
      } else if (el.tagName && el.tagName.toLowerCase() === 'a' && el.getAttribute('href')) {
        val = el.getAttribute('href');
      }

      const isDisabled = el.disabled || el.getAttribute('aria-disabled') === 'true';
      const cx = rect.left + rect.width / 2;
      const cy = rect.top + rect.height / 2;

      let occlusion = null;
      let isExactHit = false;

      if (cx >= 0 && cx <= window.innerWidth && cy >= 0 && cy <= window.innerHeight) {
        const hit = document.elementFromPoint(cx, cy);
        if (hit === el || el.contains(hit)) {
          isExactHit = true;
        } else if (hit) {
          const modalAncestor = hit.closest('dialog, [role=""dialog""], [aria-modal=""true""]');
          occlusion = modalAncestor ? 'modal_blocking' : 'occluded_same_window';
        }
      }

      const isOffscreen = rect.bottom < 0 || rect.top > window.innerHeight || rect.right < 0 || rect.left > window.innerWidth;
      const path = buildSelectorPath(el);

      let selStart = null;
      let selEnd = null;
      try {
        if (typeof el.selectionStart === 'number') selStart = el.selectionStart;
        if (typeof el.selectionEnd === 'number') selEnd = el.selectionEnd;
      } catch (e) {
        // Ignored for elements without selection
      }

      return {
        found: true,
        ref: ref,
        role: role,
        name: name,
        value: val,
        isPassword: isRedacted,
        isEnabled: !isDisabled,
        automationId: autoId,
        bounds: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
        center: { x: cx, y: cy },
        isExactHit: isExactHit,
        occlusion: occlusion,
        isOffscreen: isOffscreen,
        path: path,
        selectionStart: selStart,
        selectionEnd: selEnd
      };
    },

    isFocused: function(ref) {
      const el = registry.get(ref);
      if (!el || !el.isConnected) return { success: false, reason: 'not_found' };
      if (!isActuallyFocused(el)) return { success: false, reason: 'focus_failed' };
      return { success: true };
    },

    executeAction: function(ref, action, value) {
      const el = registry.get(ref);
      if (!el) return { success: false, reason: 'not_found' };

      if (action === 'set_value') {
        el.focus();
        el.value = value || '';
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, value: el.value };
      }

      if (action === 'clear') {
        el.focus();
        el.value = '';
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, value: '' };
      }

      if (action === 'check') {
        el.checked = true;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, checked: true };
      }

      if (action === 'uncheck') {
        el.checked = false;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, checked: false };
      }

      if (action === 'select') {
        el.value = value;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, value: el.value };
      }

      if (action === 'focus') {
        el.focus();
        if (!isActuallyFocused(el)) return { success: false, reason: 'focus_failed' };
        return { success: true };
      }

      if (action === 'scroll_into_view') {
        el.scrollIntoView({ block: 'center', inline: 'center' });
        return { success: true };
      }

      return { success: true };
    }
  };
})();
";
}
