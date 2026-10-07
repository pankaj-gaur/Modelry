// Drag-and-drop feedback for the IA upload.
document.querySelectorAll('.dropzone').forEach(zone => {
  const input = zone.querySelector('input[type=file]');
  const out = zone.querySelector('.dropzone__file');
  const show = () => { out.textContent = input.files.length ? 'Selected: ' + input.files[0].name : ''; };
  input.addEventListener('change', show);
  ['dragenter', 'dragover'].forEach(t => zone.addEventListener(t, e => { e.preventDefault(); zone.classList.add('is-over'); }));
  ['dragleave', 'drop'].forEach(t => zone.addEventListener(t, () => zone.classList.remove('is-over')));
  zone.addEventListener('drop', e => { e.preventDefault(); if (e.dataTransfer.files.length) { input.files = e.dataTransfer.files; show(); } });
  zone.closest('form')?.addEventListener('submit', ev => {
    const b = ev.target.querySelector('button[type=submit]');
    if (b && input.files.length) { b.disabled = true; b.textContent = 'Reading workbook…'; }
  });
});
