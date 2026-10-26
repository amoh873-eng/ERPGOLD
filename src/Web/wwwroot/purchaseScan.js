// =====================================================================================
// purchaseScan.js — Scanner-Burst coalescer for the Purchase-Invoice barcode-receiving field.
// -------------------------------------------------------------------------------------
// Mirrors the POS scale interceptor pattern (scaleBarcode.js): a hardware scanner fires a
// very fast burst of keydowns (typically 20–60ms apart). Without coalescing, EVERY keydown
// (including each Enter) is sent to the Blazor Server circuit, flooding it and dropping
// scans during rapid receiving. This module swallows the burst in the browser and emits a
// SINGLE injected Enter keydown per scan (with the value already committed via input+change),
// so Blazor handles exactly one event per scanned item.
//
// Human typing (gaps > scanSpeedMs) is never swallowed: each key passes through normally.
// =====================================================================================

const KEY = '__erpPurchaseScan';
const state = window[KEY] || (window[KEY] = {
    target: null,
    buf: '',
    lastMs: 0,
    burstActive: false,   // true once two+ fast chars confirm a scanner burst
    pendingTimer: null,   // waits for a possible second fast char after the first
    speedMs: 120,         // scanner burst threshold (ms). Adjust if a scanner is faster/slower.
    attached: false
});

// Refresh the target element (the dialog re-renders; re-query each attach() call).
function refreshTarget() {
    state.target = document.querySelector('input[placeholder*="امسح"], input[placeholder*="Scan barcode"]');
}

// تخبر هل العنصر هو حقل المسح (مطابقة placeholder — مستقل عن مرجع العقدة،
// فيصمد أمام إعادة رسم Blazor التي قد تستبدل العقدة أثناء الطفرة)
function isScanTarget(el) {
    if (!el || el.tagName !== 'INPUT') return false;
    const ph = el.getAttribute ? (el.getAttribute('placeholder') || '') : '';
    return ph.indexOf('امسح') >= 0 || ph.indexOf('Scan barcode') >= 0;
}

function clearPending() { if (state.pendingTimer) { clearTimeout(state.pendingTimer); state.pendingTimer = null; } }

function reset() { state.buf = ''; state.lastMs = 0; state.burstActive = false; clearPending(); }

// Commit a pending HUMAN char back into the field (append) so live typing still works.
function commitBufToInput(el) {
    if (!state.buf) return;
    const existing = el.value || '';
    const next = existing + state.buf;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    if (setter) setter.call(el, next);
    else el.value = next;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    state.buf = '';
    state.lastMs = 0;
    state.burstActive = false;
    clearPending();
}

function swallow(e) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); }

function onKeyDown(e) {
    // أحداث حقننا أنفسها — دعها تمر مباشرة إلى Blazor دون إعادة التقاط (كسر الحلقة)
    if (e.__erpScanInjected) return;
    if (e.isComposing || e.keyCode === 229) return;

    const el = e.target;
    const now = Date.now();
    const gap = state.lastMs === 0 ? Infinity : now - state.lastMs;

    // أثناء طفرة مؤكدة نتقبل أي عقدة (فقدان التركيز)؛ وإلا نطابق placeholder فقط.
    if (!state.burstActive && !isScanTarget(el)) return;

    // Enter — scanner terminator (or a human pressing Enter manually).
    if (e.key === 'Enter' || e.key === 'NumpadEnter' || e.key === 'Return') {
        if (state.burstActive && state.buf) {
            // طفرة مؤكدة: اكتب الكود الكامل في الحقل وأرسل Enter واحداً (يقرأه HandleScanKeyDown).
            swallow(e);
            const code = state.buf;
            reset();
            const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
            if (setter) setter.call(el, code);
            else el.value = code;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            const injectedEnter = new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true, cancelable: true });
            injectedEnter.__erpScanInjected = true; // لا يُلتقط مرة أخرى
            el.dispatchEvent(injectedEnter);
        } else {
            // Enter يدوي: التزم أي حرف بشري معلّق ثم دع Enter الحقيقي يمر (يقرأ HandleScanKeyDown قيمة الحقل).
            clearPending();
            if (state.buf) commitBufToInput(el);
            else reset();
        }
        return;
    }

    // أي مفتاح غير حرفي واحد يقطع الطفرة.
    if (e.key.length !== 1) { reset(); return; }

    if (state.lastMs === 0) {
        // أول حرف: ابتلعه (لا يصل إلى Blazor ← لا إعادة رسم ← لا سقوط بقية الطفرة)، وافتح نافذة طفرة.
        state.buf = e.key;
        state.lastMs = now;
        state.burstActive = false;
        clearPending();
        swallow(e);
        state.pendingTimer = setTimeout(function () { if (!state.burstActive) commitBufToInput(el); }, state.speedMs);
        return;
    }

    if (gap <= state.speedMs) {
        // طفرة مؤكدة: ابتلع واحشد الكود.
        state.burstActive = true;
        state.buf += e.key;
        state.lastMs = now;
        clearPending();
        swallow(e);
        return;
    }

    // فجوة طويلة → كتابة يدوية: التزم الحرف السابق ثم ابدأ مخزناً جديداً.
    clearPending();
    commitBufToInput(el);
    state.buf = e.key;
    state.lastMs = now;
    state.burstActive = false;
    swallow(e);
    state.pendingTimer = setTimeout(function () { if (!state.burstActive) commitBufToInput(el); }, state.speedMs);
}

// Public API used by PurchaseInvoiceFormDialog.
export function attach() {
    refreshTarget();
    if (!state.attached) {
        document.addEventListener('keydown', onKeyDown, true);
        state.attached = true;
    }
}

export function detach() {
    if (state.attached) {
        document.removeEventListener('keydown', onKeyDown, true);
        state.attached = false;
    }
    state.target = null;
    reset();
}