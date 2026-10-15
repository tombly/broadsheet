// Telerik charts draw to the pixel width they measured at render time and do not re-measure when
// their container grows, so a resized window leaves dead space beside every chart. One debounced
// listener drives them all: a chart subscribes on first render and refreshes itself when told.
window.broadsheet = (function () {
    const subscribers = new Map();
    let nextId = 0;
    let pending;

    window.addEventListener('resize', function () {
        clearTimeout(pending);
        // A drag fires resize continuously; redrawing every chart per frame is what makes it crawl.
        pending = setTimeout(function () {
            subscribers.forEach(function (subscriber) {
                subscriber.invokeMethodAsync('OnViewportResized');
            });
        }, 150);
    });

    return {
        subscribeResize: function (subscriber) {
            const id = ++nextId;
            subscribers.set(id, subscriber);
            return id;
        },
        unsubscribeResize: function (id) {
            subscribers.delete(id);
        }
    };
})();
