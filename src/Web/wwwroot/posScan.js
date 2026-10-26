// =====================================================================================
// posScan.js — UNIFIED scanner capture + classification for the POS.
// -------------------------------------------------------------------------------------
// ONE capture listener (document keydown, capture phase). It swallows the ENTIRE scanner
// burst (including the first char) so no scanner keystroke ever reaches <input>/Blazor
// (SEARCH_VALUE_AFTER stays ""). On Enter it classifies the full code and routes it:
//   * Scale barcode   -> the EXISTING scale parser (scaleBarcode.js tryParse) -> ReceiveScaleBarcode
//   * Normal barcode  -> HandleScannedCode (regular POS search)
// scaleBarcode.js no longer attaches its own capture listener (see scaleBarcode.js).
// Human typing (slow gaps) is committed back to the field so live search still works and
// never triggers a scan.
// =====================================================================================

window.__posScanVersion = "UNIFIED-01";
console.log("[POS-SCAN] loaded UNIFIED-01");

const KEY = '__erpPosScan';
const state = window[KEY] || (window[KEY] = {
    buf: '',          // characters of the current burst (or pending human char)
    lastMs: 0,        // timestamp of the last swallowed key
    burstActive: false, // true once two+ fast chars confirm a scanner burst
    pendingTimer: null, // waits for a possible second fast char after the first
    BURST_MS: 120,    // chars closer than this belong to the same scan burst
    dotNetRef: null,  // DotNetObjectReference to Pos.razor
    attached: false
});

function clearPending() { if (state.pendingTimer) { clearTimeout(state.pendingTimer); state.pendingTimer = null; } }

function reset() {
    state.buf = '';
    state.lastMs = 0;
    state.burstActive = false;
    clearPending();
}

// Target the POS search field (selector-based so it survives Blazor re-renders).
function isScanTarget(el) {
    if (!el || el.tagName !== 'INPUT') return false;
    return !!el.closest && !!el.closest('.pos-search');
}

// Commit a pending HUMAN char back into the field (append) so live search works.
function commitBufToInput(el) {
    if (!state.buf) return;
    const existing = el.value || '';
    const next = existing + state.buf;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    if (setter) setter.call(el, next);
    else el.value = next;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    console.log("[POS-SCAN] human char committed to field='" + next + "'");
    state.buf = '';
    state.lastMs = 0;
    state.burstActive = false;
    clearPending();
}

function swallow(e) {
    e.preventDefault();
    e.stopPropagation();
    e.stopImmediatePropagation();
}

// Classify the full captured code. Scale barcodes are identified by the EXISTING
// scaleBarcode.js parser (tryParse) — the scale parsing is NOT reinvented here.
// Returns the scale payload (truthy) when it's a scale barcode, otherwise null (normal).
function classifyScale(code) {
    if (typeof window.__erpScaleBarcodeTryParse === 'function') {
        try { return window.__erpScaleBarcodeTryParse(code) || null; }
        catch (err) { console.error("[POS-SCAN] scale classify error", err); return null; }
    }
    return null; // scale parser not loaded -> treat as normal
}

function onKeyDown(e) {
    if (e.isComposing || e.keyCode === 229) return;

    const el = e.target;
    const isScan = isScanTarget(el);
    const now = Date.now();
    const gap = state.lastMs === 0 ? Infinity : now - state.lastMs;

    // If we are mid-burst we swallow regardless of the (possibly lost) focus target.
    if (!isScan && !state.burstActive) return;

    // Enter — scanner terminator (or a human pressing Enter manually).
    if (e.key === 'Enter' || e.key === 'NumpadEnter' || e.key === 'Return') {
        if (state.burstActive && state.buf) {
            // Confirmed scanner burst -> classify, then route to the right .NET handler.
            console.log("[POS-SCAN] ENTER captured");
            console.log("[POS-SCAN] BUFFER=" + state.buf);
            swallow(e);
            const code = state.buf;
            reset();
            const parsed = classifyScale(code);
            if (parsed) {
                // Scale barcode -> existing scale path (ReceiveScaleBarcode) — parser NOT reinvented.
                console.log("[POS-SCAN] CLASS=Scale");
                const sState = window.__erpScaleBarcodeState;
                if (sState && sState.dotNet) {
                    sState.dotNet.invokeMethodAsync('ReceiveScaleBarcode', parsed)
                        .catch(function (err) { console.error("[POS-SCAN] FAILED (scale route)", err); });
                } else {
                    console.error("[POS-SCAN] scale dotNetRef missing — cannot route scale barcode");
                }
            } else {
                // Normal barcode -> HandleScannedCode (regular POS search).
                console.log("[POS-SCAN] CLASS=Normal");
                if (state.dotNetRef) {
                    state.dotNetRef.invokeMethodAsync('HandleScannedCode', code)
                        .then(function (r) { console.log("[POS-SCAN] C# INVOKED result=" + JSON.stringify(r)); })
                        .catch(function (err) { console.error("[POS-SCAN] JS->C# FAILED", err); });
                } else {
                    console.error("[POS-SCAN] dotNetRef is NULL — cannot call HandleScannedCode");
                }
            }
        } else {
            // Human Enter: commit any pending human char(s), then let the real Enter flow.
            console.log("[POS-SCAN] Enter (human path) buf=" + JSON.stringify(state.buf) + " — committing then letting flow");
            clearPending();
            if (state.buf) commitBufToInput(el);
            else reset();
        }
        return;
    }

    // Any non-single-char key resets a pending burst (only plain characters matter).
    if (e.key.length !== 1) { reset(); return; }

    if (state.lastMs === 0) {
        // FIRST char: swallow it so it never reaches <input>/Blazor. Open a burst window.
        console.log("[POS-SCAN] scanner burst START (first key buffered)");
        state.buf = e.key;
        state.lastMs = now;
        state.burstActive = false;
        clearPending();
        swallow(e);
        console.log("[POS-SCAN] key=" + e.key + " captured");
        state.pendingTimer = setTimeout(function () {
            if (!state.burstActive) {
                console.log("[POS-SCAN] no second char within BURST_MS -> human typing, commit");
                commitBufToInput(el);
            }
        }, state.BURST_MS);
        return;
    }

    if (gap <= state.BURST_MS) {
        // Confirmed burst: swallow this char and accumulate the code.
        state.burstActive = true;
        state.buf += e.key;
        state.lastMs = now;
        clearPending();
        swallow(e);
        console.log("[POS-SCAN] key=" + e.key + " captured");
        return;
    }

    // Gap too long -> human typing: commit the previous char, then start a new buffer.
    console.log("[POS-SCAN] gap=" + gap + "ms too long -> human typing");
    clearPending();
    commitBufToInput(el);
    state.buf = e.key;
    state.lastMs = now;
    state.burstActive = false;
    swallow(e);
    console.log("[POS-SCAN] key=" + e.key + " captured (after human gap)");
    state.pendingTimer = setTimeout(function () {
        if (!state.burstActive) {
            console.log("[POS-SCAN] no second char within BURST_MS -> human typing, commit");
            commitBufToInput(el);
        }
    }, state.BURST_MS);
}


// Public API used by Pos.razor.
export function attach(dotNetRef) {
    console.log("[POS-SCAN] attach() called. dotNetRef present=" + (!!dotNetRef) + " attached(was)=" + state.attached);
    const searchEl = document.querySelector('.pos-search input');
    console.log("[POS-SCAN] .pos-search input found=" + (!!searchEl));
    state.dotNetRef = dotNetRef || null;
    if (!state.attached) {
        document.addEventListener('keydown', onKeyDown, true);
        state.attached = true;
        console.log("[POS-SCAN] keydown listener installed on document (capture=true)");
    }
}

export function detach() {
    console.log("[POS-SCAN] detach() called");
    if (state.attached) {
        document.removeEventListener('keydown', onKeyDown, true);
        state.attached = false;
    }
    state.dotNetRef = null;
    reset();
}

