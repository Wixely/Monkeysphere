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
