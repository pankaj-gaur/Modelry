// Filter the findings table by level.
document.querySelectorAll('[data-issue-filters]').forEach(group => {
  const table = group.nextElementSibling.querySelector('[data-issues]');
  group.addEventListener('click', e => {
    const btn = e.target.closest('button[data-level]'); if (!btn) return;
    group.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b === btn)));
    table.querySelectorAll('tbody tr').forEach(tr => tr.hidden = btn.dataset.level !== 'all' && tr.dataset.level !== btn.dataset.level);
  });
});
