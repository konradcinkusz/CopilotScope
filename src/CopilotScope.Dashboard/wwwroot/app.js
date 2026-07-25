window.scrollToBottom = (el) => {
    if (el) el.scrollTop = el.scrollHeight;
};

window.focusElement = (el) => {
    if (el) el.focus();
};

// View preferences (detail level, rail toggles) survive a reload. Storage throws in some
// private-browsing modes, so every access is guarded — the UI falls back to its defaults.
window.scopePrefs = {
    load: () => {
        try { return localStorage.getItem("copilotscope.prefs"); } catch { return null; }
    },
    save: (json) => {
        try { localStorage.setItem("copilotscope.prefs", json); } catch { /* ignore */ }
    }
};
