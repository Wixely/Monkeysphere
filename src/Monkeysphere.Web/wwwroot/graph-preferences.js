function recordTypeKey(domainId) {
    return `monkeysphere.graph.record-types.${domainId}`;
}

const unsavedMessage = 'You have unsaved graph changes. Leave this page and discard them?';
let unsavedChangesEnabled = false;

function beforeUnload(event) {
    if (!unsavedChangesEnabled) {
        return;
    }

    event.preventDefault();
    event.returnValue = '';
}

function followLink(event) {
    if (!unsavedChangesEnabled || event.defaultPrevented || event.button !== 0 ||
        event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
        return;
    }

    const link = event.target?.closest?.('a[href]');
    if (!link || link.target === '_blank' || link.hasAttribute('download')) {
        return;
    }

    const destination = new URL(link.href, document.baseURI);
    if (destination.href === globalThis.location.href ||
        (destination.origin === globalThis.location.origin &&
         destination.pathname === globalThis.location.pathname &&
         destination.search === globalThis.location.search &&
         destination.hash !== globalThis.location.hash)) {
        return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    if (globalThis.confirm(unsavedMessage)) {
        unsavedChangesEnabled = false;
        globalThis.location.assign(destination.href);
    }
}

function submitForm(event) {
    if (!unsavedChangesEnabled) {
        return;
    }

    if (!globalThis.confirm(unsavedMessage)) {
        event.preventDefault();
        event.stopImmediatePropagation();
    } else {
        unsavedChangesEnabled = false;
    }
}

// Fullscreen is requested on the page's graph stage rather than on the canvas alone. The record
// centring combobox is the graph's only non-visual alternative and is a sibling of the canvas, so
// fullscreening the canvas by itself would take that alternative off the screen with it.
let fullscreenTarget = null;
let fullscreenCallback = null;

function isFullscreen() {
    return Boolean(fullscreenTarget) && document.fullscreenElement === fullscreenTarget;
}

// State is only ever reported from this event, never assumed from a request that appeared to
// succeed, so leaving fullscreen by Escape or by the browser's own chrome is seen the same way as
// pressing the button.
function fullscreenChanged() {
    fullscreenCallback?.invokeMethodAsync('FullscreenChanged', isFullscreen());
}

export function initFullscreen(element, callback) {
    if (!element) {
        return false;
    }

    fullscreenTarget = element;
    fullscreenCallback = callback;
    document.addEventListener('fullscreenchange', fullscreenChanged);
    // Unprefixed only. Where that is missing the caller withholds the control rather than offering
    // a button that would do nothing.
    return document.fullscreenEnabled === true && typeof element.requestFullscreen === 'function';
}

export async function toggleFullscreen() {
    if (!fullscreenTarget) {
        return false;
    }

    try {
        if (isFullscreen()) {
            await document.exitFullscreen();
        } else {
            await fullscreenTarget.requestFullscreen();
        }

        return true;
    } catch {
        // Refused: no user activation, a permissions policy, or an element the browser will not
        // present. The caller says so rather than showing a control stuck in the wrong state.
        return false;
    }
}

export async function disposeFullscreen() {
    document.removeEventListener('fullscreenchange', fullscreenChanged);
    if (isFullscreen()) {
        try {
            // Leaving the page while still fullscreen would strand the browser there.
            await document.exitFullscreen();
        } catch {
            // Already leaving; nothing useful remains to do.
        }
    }

    fullscreenTarget = null;
    fullscreenCallback = null;
}

// What the operator reached for last, kept per browser like the record-type filter above. This is
// a convenience rather than data: losing it costs nothing, so it does not warrant a round trip or
// a column, and a hardened browser that refuses storage simply gets an unordered list.
// Deliberately not scoped to a domain or a record: which part of the menu somebody is working in
// is about what they are doing, not about what they are looking at, and carrying it across is the
// point of remembering it at all.
export function loadSetting(key, fallback) {
    try {
        return globalThis.localStorage.getItem(`monkeysphere.graph.${key}`) ?? fallback;
    } catch {
        return fallback;
    }
}

export function saveSetting(key, value) {
    try {
        globalThis.localStorage.setItem(`monkeysphere.graph.${key}`, value);
    } catch {
        // Storage may be unavailable in a hardened or private browser context.
    }
}

// Scoped to a domain, unlike loadSetting above, and the difference is not incidental. Whether the
// graph saves itself is a decision about one body of records: a domain being tidied wants it on,
// and another being explored carefully wants it off, and carrying one answer across both would
// silently save work somebody was only trying out.
export function loadDomainSetting(key, domainId, fallback) {
    try {
        return globalThis.localStorage.getItem(`monkeysphere.graph.${key}.${domainId}`) ?? fallback;
    } catch {
        return fallback;
    }
}

export function saveDomainSetting(key, domainId, value) {
    try {
        globalThis.localStorage.setItem(`monkeysphere.graph.${key}.${domainId}`, value);
    } catch {
        // Storage may be unavailable in a hardened or private browser context.
    }
}

export function loadRecent(kind, domainId) {
    try {
        const value = globalThis.localStorage.getItem(`monkeysphere.graph.recent.${kind}.${domainId}`);
        const parsed = value ? JSON.parse(value) : null;
        return Array.isArray(parsed) && parsed.every(item => typeof item === 'string') ? parsed : [];
    } catch {
        return [];
    }
}

export function rememberRecent(kind, domainId, value, keep) {
    try {
        const existing = loadRecent(kind, domainId).filter(item => item !== value);
        const next = [value, ...existing].slice(0, keep);
        globalThis.localStorage.setItem(`monkeysphere.graph.recent.${kind}.${domainId}`, JSON.stringify(next));
        return next;
    } catch {
        return [value];
    }
}

export function loadRecordTypeIds(domainId) {
    try {
        const value = globalThis.localStorage.getItem(recordTypeKey(domainId));
        const parsed = value ? JSON.parse(value) : null;
        return Array.isArray(parsed) && parsed.every(item => typeof item === 'string') ? parsed : null;
    } catch {
        return null;
    }
}

export function saveRecordTypeIds(domainId, ids) {
    try {
        globalThis.localStorage.setItem(recordTypeKey(domainId), JSON.stringify(ids));
    } catch {
        // Storage may be unavailable in a hardened or private browser context.
    }
}

export function setUnsavedChanges(enabled) {
    const next = Boolean(enabled);
    if (next === unsavedChangesEnabled) {
        return;
    }

    unsavedChangesEnabled = next;
    if (next) {
        globalThis.addEventListener('beforeunload', beforeUnload);
        document.addEventListener('click', followLink, true);
        document.addEventListener('submit', submitForm, true);
    } else {
        globalThis.removeEventListener('beforeunload', beforeUnload);
        document.removeEventListener('click', followLink, true);
        document.removeEventListener('submit', submitForm, true);
    }
}

export function clearUnsavedChanges() {
    setUnsavedChanges(false);
}
