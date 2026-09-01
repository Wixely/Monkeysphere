(() => {
    const storageKey = 'monkeysphere.theme';
    const themes = new Set(['light', 'dark']);

    function storedTheme() {
        try {
            const value = globalThis.localStorage.getItem(storageKey);
            return themes.has(value) ? value : null;
        } catch {
            return null;
        }
    }

    function updateControls(theme) {
        const isDark = theme === 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.setAttribute('aria-pressed', String(isDark));
            button.setAttribute('aria-label', isDark ? 'Use light theme' : 'Use dark theme');
            button.setAttribute('title', isDark ? 'Use light theme' : 'Use dark theme');

            const icon = button.querySelector('[data-theme-icon]');
            if (icon) {
                icon.textContent = isDark ? '\u2600' : '\u263E';
            }
        });
    }

    function apply(theme, persist) {
        const selected = themes.has(theme) ? theme : 'light';
        document.documentElement.dataset.theme = selected;
        document.documentElement.style.colorScheme = selected;

        if (persist) {
            try {
                globalThis.localStorage.setItem(storageKey, selected);
            } catch {
                // The theme still works when browser storage is unavailable.
            }
        }

        updateControls(selected);
        globalThis.dispatchEvent(new CustomEvent('monkeysphere:themechanged', {
            detail: { theme: selected }
        }));
        return selected;
    }

    const initialTheme = storedTheme() ?? 'light';
    apply(initialTheme, false);

    globalThis.monkeysphereTheme = {
        current: () => document.documentElement.dataset.theme ?? 'light',
        set: theme => apply(theme, true),
        toggle: () => apply(
            document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark',
            true)
    };

    document.addEventListener('click', event => {
        if (event.target.closest('[data-theme-toggle]')) {
            globalThis.monkeysphereTheme.toggle();
        }
    });

    document.addEventListener('DOMContentLoaded', () => {
        updateControls(initialTheme);
        globalThis.Blazor?.addEventListener('enhancedload', () => {
            apply(storedTheme() ?? 'light', false);
        });
    });
    globalThis.addEventListener('storage', event => {
        if (event.key === storageKey && themes.has(event.newValue)) {
            apply(event.newValue, false);
        }
    });
})();
