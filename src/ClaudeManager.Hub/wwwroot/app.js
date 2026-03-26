window.claudeManager = {
    // Scroll to bottom only if the user is within 150px of the bottom,
    // so we don't fight them when they've scrolled up to read history.
    scrollToBottomIfNear: function (element) {
        if (!element) return;
        const threshold = 150;
        const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        if (distanceFromBottom <= threshold) {
            element.scrollTop = element.scrollHeight;
        }
    },

    scrollToBottom: function (element) {
        if (!element) return;
        element.scrollTop = element.scrollHeight;
    },

    // ── File download ─────────────────────────────────────────────────────

    downloadText: function (filename, content) {
        const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href     = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    },

    // ── Browser notifications ──────────────────────────────────────────────

    getNotificationPermission: function () {
        if (!('Notification' in window)) return 'denied';
        return Notification.permission;
    },

    requestNotificationPermission: async function () {
        if (!('Notification' in window)) return 'denied';
        return await Notification.requestPermission();
    },

    // Shows a desktop notification only when the tab is not currently focused.
    // When the tab is visible the in-app toast is sufficient.
    showBrowserNotification: function (title, body, tag) {
        if (!('Notification' in window)) return;
        if (Notification.permission !== 'granted') return;
        if (document.visibilityState === 'visible') return;
        new Notification(title, { body, tag });
    }
};
