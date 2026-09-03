const recordTypeKey = 'monkeysphere.graph.record-types';

export function loadRecordTypeIds() {
    try {
        const value = globalThis.localStorage.getItem(recordTypeKey);
        const parsed = value ? JSON.parse(value) : null;
        return Array.isArray(parsed) && parsed.every(item => typeof item === 'string') ? parsed : null;
    } catch {
        return null;
    }
}

export function saveRecordTypeIds(ids) {
    try {
        globalThis.localStorage.setItem(recordTypeKey, JSON.stringify(ids));
    } catch {
        // Storage may be unavailable in a hardened or private browser context.
    }
}
