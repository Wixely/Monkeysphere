function recordTypeKey(domainId) {
    return `monkeysphere.graph.record-types.${domainId}`;
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
