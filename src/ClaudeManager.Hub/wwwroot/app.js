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
    }
};
