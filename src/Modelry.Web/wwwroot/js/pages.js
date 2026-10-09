// Pages step: each thumbnail is the real page in a sandboxed frame. The frame scrolls itself to its section and reports
// the section's size; here the frame is made exactly that tall and scaled down to the thumbnail width.
(function () {
    'use strict';
    var WIDTH = 1280, MAX_HEIGHT = 320;
    var thumbs = Array.prototype.slice.call(document.querySelectorAll('[data-thumb]'));

    // left/width: the section's horizontal position in the 1280px-wide page, so a block that sits beside another
    // (e.g. a download card next to a widget) is cropped to itself.
    function layout(box, height, left, width) {
        var frame = box.querySelector('iframe');
        width = width > 0 ? Math.min(width, WIDTH) : WIDTH;
        left = width < WIDTH ? Math.max(0, left || 0) : 0;
        var scale = box.clientWidth / width;
        frame.style.width = WIDTH + 'px';
        frame.style.height = Math.ceil(height) + 'px';
        frame.style.transform = 'scale(' + scale + ') translateX(' + (-left) + 'px)';
        box.style.height = Math.min(MAX_HEIGHT, Math.max(90, Math.ceil(height * scale))) + 'px';
        box.classList.add('is-ready');
        box.dataset.height = height; box.dataset.left = left; box.dataset.width = width;
    }

    window.addEventListener('message', function (e) {
        var data = e.data;
        if (!data || data.modelry !== 'section') return;
        for (var i = 0; i < thumbs.length; i++) {
            var frame = thumbs[i].querySelector('iframe');
            if (frame && frame.contentWindow === e.source) { layout(thumbs[i], Number(data.height) || 300, Number(data.left) || 0, Number(data.width) || WIDTH); return; }
        }
    });

    window.addEventListener('resize', function () {
        thumbs.forEach(function (box) { if (box.dataset.height) layout(box, Number(box.dataset.height), Number(box.dataset.left), Number(box.dataset.width)); });
    });

    // Excluded rows look muted as soon as the choice changes.
    document.querySelectorAll('.section-row select').forEach(function (s) {
        s.addEventListener('change', function () {
            s.closest('.section-row').classList.toggle('is-excluded', s.value === 'exclude');
        });
    });
})();
