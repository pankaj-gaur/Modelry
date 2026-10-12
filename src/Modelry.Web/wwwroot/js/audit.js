// Audit: live progress while the publication is read, then dashboard tiles → detail table → item pop-up.
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => { const d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; };
  const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

  // ------------------------------------------------------------------ progress
  const progress = $('audit-progress');
  if (progress) {
    const id = progress.dataset.id;
    const fmt = (sec) => Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
    let timer = null, failures = 0;

    function render(s) {
      $('bar').style.width = s.percent + '%';
      $('bar').classList.toggle('failed', s.state === 'Failed');
      $('bar').classList.toggle('cancelled', s.state === 'Cancelled');
      $('bar').parentElement.setAttribute('aria-valuenow', s.percent);
      $('pct').textContent = s.percent + '%';
      $('step-label').textContent = {
        Completed: 'Finished – opening the dashboard…',
        Cancelled: 'Stopped – nothing was changed.',
        Failed: 'The audit stopped with an error.'
      }[s.state] || ('Step ' + s.step + ' of ' + s.stepCount + ': ' + s.stepName);
      $('item').textContent = s.state === 'Running' ? s.activity : '';
      $('elapsed').textContent = fmt(s.elapsedSeconds);
      // Last step = AI summary: the scan is done, so Stop only skips the summary.
      if (s.summaryStep && !$('cancel').disabled) $('cancel').textContent = 'Skip the summary';
      document.querySelectorAll('#steps li').forEach(li => {
        const n = +li.dataset.step;
        li.className = n < s.step || s.state === 'Completed' ? 'done' : n === s.step ? (s.state === 'Running' ? 'active' : 'stopped') : 'pending';
      });
      if (s.state !== 'Running') {
        clearInterval(timer);
        $('cancel').hidden = true;
        if (s.state === 'Completed') { setTimeout(() => window.location.reload(), 400); return; }
        $('retry').hidden = false;
        $('back').hidden = false;
        if (s.error) { $('failed').hidden = false; $('failed-text').textContent = s.error; }
      }
    }

    async function poll() {
      try {
        const r = await fetch('/api/audit/' + id + '/status', { headers: { Accept: 'application/json' } });
        if (r.status === 401) { window.location = '/Account/Login?expired=true'; return; }
        if (!r.ok) throw new Error(r.status);
        failures = 0;
        render(await r.json());
      } catch (e) {
        if (++failures > 10) { clearInterval(timer); $('item').textContent = 'Lost contact with the server – reload the page to check on the audit.'; }
      }
    }

    $('cancel').addEventListener('click', async () => {
      $('cancel').disabled = true;
      $('cancel').textContent = $('cancel').textContent === 'Skip the summary' ? 'Skipping…' : 'Stopping…';
      await fetch('/api/audit/' + id + '/cancel', { method: 'POST', headers: { RequestVerificationToken: token() } });
    });

    if (progress.dataset.state === 'Running') { poll(); timer = setInterval(poll, 1000); } else poll();
    return;
  }

  // ------------------------------------------------------------------ dashboard
  const modelEl = $('audit-model');
  if (!modelEl) return;
  const model = JSON.parse(modelEl.textContent);
  const kinds = Object.fromEntries(model.kinds.map(k => [k.key, k]));
  const state = { kind: null, q: '', origin: '', publish: '', finding: '', sort: 'path', desc: false, page: 1, matching: 0, pageSize: +($('page-size')?.value || 20) };
  const fstate = { rule: null, page: 1, pageSize: +($('f-page-size')?.value || 20) };
  let searchTimer = null, request = 0;

  // A date as yyyy-MM-dd, with the time in the tooltip (keeps the table narrow).
  const day = (v) => '<span class="nowrap" title="' + esc(v) + '">' + esc(String(v).slice(0, 10)) + '</span>';
  const na = (reason) => '<span class="na" title="' + esc(reason) + '">n/a</span>';

  function columns(kind) {
    const k = kinds[kind];
    const cols = [{ key: 'title', label: 'Title', cell: r => '<span class="cell-title">' + esc(r.title) + '</span><span class="cell-id">' + esc(r.id) + '</span>' }];
    if (k.pathLabel) cols.push({ key: 'path', label: k.pathLabel, cell: r => r.path ? esc(r.path) : '<span class="muted">(root)</span>' });
    k.columns.forEach(c => cols.push({
      key: c.key, label: c.label, na: c.na, numeric: c.numeric,
      cell: r => c.na ? '<span class="muted">—</span>' : esc(r.values[c.key] ?? '')
    }));
    if (k.publishable) {
      cols.push({ key: 'publish', label: 'Publish status', na: k.na.publish, cell: publishCell });
      // One column per publish target (e.g. Staging, Live) with the date it was last published there.
      (model.targets || []).forEach(t => cols.push({ key: 'target:' + t, label: t, na: k.na.publish, cell: r => {
        const hit = r.targets.find(x => x.target.toLowerCase() === t.toLowerCase());
        return hit ? (hit.at ? day(hit.at) : 'Yes') : '<span class="muted">—</span>';
      } }));
    }
    cols.push({ key: 'findings', label: 'Findings', nosort: true, cell: r => r.findings.length
      ? r.findings.map(f => '<span class="sev-pill sev--' + esc(model.rules[f]?.severity || 'Info') + '" title="' + esc(model.rules[f]?.title || f) + '">' + esc(f) + '</span>').join('')
      : '' });
    cols.push({ key: 'origin', label: model.originLabel || 'BluePrint', na: k.na.origin, cell: r => '<span class="origin origin--' + esc(r.originKey) + '">' + esc(r.origin) + '</span>' +
      (r.from ? '<span class="cell-from">' + (model.cms === 'aem' ? 'of ' : 'from ') + esc(r.from) + '</span>' : '') });
    // Date and time, with the user below, in one column each (sorted by date; search also finds the user).
    const when = (date, user, naDate, naUser) =>
      (naDate ? '' : date ? '<span class="nowrap">' + esc(date) + '</span>' : '<span class="muted">—</span>') +
      (naUser ? '' : user ? '<span class="cell-from">' + esc(user) + '</span>' : '');
    cols.push({ key: 'created', label: 'Created', na: k.na.created && k.na.createdBy, cell: r => when(r.created, r.createdBy, k.na.created, k.na.createdBy) });
    cols.push({ key: 'modified', label: 'Last modified', na: k.na.modified && k.na.modifiedBy, cell: r => when(r.modified, r.modifiedBy, k.na.modified, k.na.modifiedBy) });
    // Columns this audit could not fill are left out (listed under the table) rather than shown empty.
    const byReason = {};
    cols.filter(c => c.na).forEach(c => (byReason[c.na] = byReason[c.na] || []).push(c.label));
    columns.hidden = Object.entries(byReason).map(([reason, labels]) => labels.join(', ') + ' – ' + reason.replace(/\.$/, ''));
    return cols.filter(c => !c.na);
  }

  function publishCell(r) {
    const label = { Published: 'Published', Unpublished: 'Not published', Unknown: 'Unknown' }[r.publish] || '';
    return label ? '<span class="status status--' + esc(r.publish) + '">' + label + '</span>' : '';
  }

  function open(kind) {
    state.kind = kind; state.q = ''; state.origin = ''; state.publish = ''; state.finding = ''; state.sort = 'path'; state.desc = false; state.page = 1;
    document.querySelectorAll('.tile').forEach(t => t.setAttribute('aria-pressed', t.dataset.kind === kind ? 'true' : 'false'));
    const k = kinds[kind];
    $('detail').hidden = false;
    $('detail-title').textContent = k.title;
    $('detail-search').value = '';
    $('publish-filter').hidden = !k.publishable || !!k.na.publish;
    setPressed('origin-filter', ''); setPressed('publish-filter', ''); setPressed('finding-filter', '');
    renderHead();
    load();
    $('detail').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function setPressed(groupId, value) {
    $(groupId).querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', b.dataset.value === value ? 'true' : 'false'));
  }

  function renderHead() {
    const cols = columns(state.kind);
    let note = $('detail-hidden');
    if (!note) {
      note = document.createElement('p');
      note.id = 'detail-hidden';
      note.className = 'muted small detail-hidden';
      $('detail').querySelector('.table-wrap').before(note);
    }
    note.hidden = columns.hidden.length === 0;
    note.textContent = columns.hidden.length ? 'Not shown for this audit: ' + columns.hidden.join('; ') : '';
    $('detail-head').innerHTML = cols.map(c => {
      const sorted = state.sort === c.key;
      return '<th scope="col" aria-sort="' + (sorted ? (state.desc ? 'descending' : 'ascending') : 'none') + '">' +
        (c.nosort ? '<span class="sort">' + esc(c.label) + '</span>'
          : '<button type="button" class="sort" data-key="' + esc(c.key) + '">' + esc(c.label) + (sorted ? (state.desc ? ' ▾' : ' ▴') : '') + '</button>') +
        (c.na ? ' ' + na(c.na) : '') + '</th>';
    }).join('');
  }

  async function load() {
    const id = ++request;
    const cols = columns(state.kind);
    $('detail-body').innerHTML = '<tr><td colspan="' + cols.length + '" class="muted">Loading…</td></tr>';
    const params = new URLSearchParams({ kind: state.kind, q: state.q, origin: state.origin, publish: state.publish, finding: state.finding, sort: state.sort, desc: state.desc, page: state.page, pageSize: state.pageSize });
    try {
      const r = await fetch('/api/audit/' + model.jobId + '/rows?' + params, { headers: { Accept: 'application/json' } });
      if (r.status === 401) { window.location = '/Account/Login?expired=true'; return; }
      const data = await r.json();
      if (!r.ok) throw new Error(data.error || r.status);
      if (id !== request) return;
      state.matching = data.matching; state.pageSize = data.pageSize;
      $('detail-count').textContent = data.matching === data.total ? data.total.toLocaleString() + ' items'
        : data.matching.toLocaleString() + ' of ' + data.total.toLocaleString() + ' items';
      $('detail-body').innerHTML = data.rows.length
        ? data.rows.map(row => '<tr tabindex="0" data-id="' + esc(row.id) + '">' + cols.map(c => '<td>' + c.cell(row) + '</td>').join('') + '</tr>').join('')
        : '<tr><td colspan="' + cols.length + '" class="muted">' + (data.total === 0 ? 'None in this publication.' : 'Nothing matches the filters.') + '</td></tr>';
      const pages = Math.max(1, Math.ceil(data.matching / data.pageSize));
      $('page-info').textContent = 'Page ' + data.page + ' of ' + pages;
      $('prev').disabled = data.page <= 1;
      $('next').disabled = data.page >= pages;
    } catch (e) {
      if (id === request) $('detail-body').innerHTML = '<tr><td colspan="' + cols.length + '" class="bad">Could not load the table: ' + esc(e.message) + '</td></tr>';
    }
  }

  // ------------------------------------------------------------------ item pop-up
  const dialog = $('item-dialog');
  async function showItem(itemId, kind) {
    kind = kind || state.kind;
    const k = kinds[kind];
    $('item-kind').textContent = k.singular;
    $('item-title').textContent = 'Loading…';
    $('item-id').textContent = itemId;
    $('item-body').innerHTML = '<p class="muted">Reading the item from the CMS…</p>';
    if (!dialog.open) dialog.showModal();
    try {
      const r = await fetch('/api/audit/' + model.jobId + '/item?' + new URLSearchParams({ kind, itemId }), { headers: { Accept: 'application/json' } });
      if (r.status === 401) { window.location = '/Account/Login?expired=true'; return; }
      const d = await r.json();
      if (!r.ok) throw new Error(d.error || r.status);
      $('item-title').textContent = d.title;
      $('item-body').innerHTML = d.sections.map(s =>
        '<section class="detail-section"><h3>' + esc(s.title) + '</h3>' +
        (s.rows.length ? '<dl class="fields">' + s.rows.map(r => '<dt>' + esc(r.label) + '</dt><dd>' + esc(r.value) + '</dd>').join('') + '</dl>' : '') +
        (s.items.length ? '<ul class="detail-list' + (s.items.length > 8 ? ' detail-list--cols' : '') + '">' + s.items.map(i => '<li>' + esc(i) + '</li>').join('') + '</ul>' : '') +
        (s.xml ? '<pre class="xml">' + esc(s.xml) + '</pre>' : '') +
        '</section>').join('');
    } catch (e) {
      $('item-title').textContent = 'Could not read the item';
      $('item-body').innerHTML = '<div class="alert alert--error"><p>' + esc(e.message) + '</p></div>';
    }
  }

  // ------------------------------------------------------------------ findings
  async function loadFinding() {
    const rule = model.rules[fstate.rule];
    const body = $('finding-body');
    $('finding-detail').hidden = false;
    $('finding-title').textContent = rule ? rule.title : fstate.rule;
    $('finding-desc').textContent = document.querySelector('.rule[data-rule="' + fstate.rule + '"]')?.title || '';
    body.innerHTML = '<tr><td colspan="4" class="muted">Loading…</td></tr>';
    try {
      const r = await fetch('/api/audit/' + model.jobId + '/findings?' + new URLSearchParams({ rule: fstate.rule, page: fstate.page, pageSize: fstate.pageSize }), { headers: { Accept: 'application/json' } });
      if (r.status === 401) { window.location = '/Account/Login?expired=true'; return; }
      const d = await r.json();
      if (!r.ok) throw new Error(d.error || r.status);
      $('finding-count').textContent = d.total.toLocaleString() + ' items';
      body.innerHTML = d.rows.map(x => '<tr tabindex="0" data-id="' + esc(x.id) + '" data-kind="' + esc(x.kind) + '">' +
        '<td><span class="cell-title">' + esc(x.title) + '</span><span class="cell-id">' + esc(x.id) + '</span></td>' +
        '<td>' + esc(x.kindTitle) + '</td><td>' + (x.path ? esc(x.path) : '<span class="muted">(root)</span>') + '</td><td>' + esc(x.message) + '</td></tr>').join('');
      const pages = Math.max(1, Math.ceil(d.total / d.pageSize));
      $('f-page-info').textContent = 'Page ' + d.page + ' of ' + pages;
      $('f-prev').disabled = d.page <= 1;
      $('f-next').disabled = d.page >= pages;
    } catch (e) {
      body.innerHTML = '<tr><td colspan="4" class="bad">Could not load the findings: ' + esc(e.message) + '</td></tr>';
    }
  }
  document.querySelectorAll('.rule[data-rule]').forEach(b => b.addEventListener('click', () => {
    document.querySelectorAll('.rule').forEach(x => x.setAttribute('aria-pressed', x === b ? 'true' : 'false'));
    fstate.rule = b.dataset.rule; fstate.page = 1;
    loadFinding();
    $('finding-detail').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }));
  if ($('finding-detail')) {
    $('f-prev').addEventListener('click', () => { fstate.page--; loadFinding(); });
    $('f-next').addEventListener('click', () => { fstate.page++; loadFinding(); });
    $('finding-body').addEventListener('click', (e) => { const tr = e.target.closest('tr[data-id]'); if (tr) showItem(tr.dataset.id, tr.dataset.kind); });
    $('finding-body').addEventListener('keydown', (e) => {
      const tr = e.target.closest('tr[data-id]');
      if (tr && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); showItem(tr.dataset.id, tr.dataset.kind); }
    });
  }

  // ------------------------------------------------------------------ events
  document.querySelectorAll('.tile').forEach(t => t.addEventListener('click', () => open(t.dataset.kind)));
  $('detail-head').addEventListener('click', (e) => {
    const b = e.target.closest('button.sort');
    if (!b) return;
    state.desc = state.sort === b.dataset.key ? !state.desc : false;
    state.sort = b.dataset.key; state.page = 1;
    renderHead(); load();
  });
  $('detail-search').addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { state.q = $('detail-search').value.trim(); state.page = 1; load(); }, 250);
  });
  [['origin-filter', 'origin'], ['publish-filter', 'publish'], ['finding-filter', 'finding']].forEach(([groupId, key]) =>
    $(groupId).addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      state[key] = b.dataset.value; state.page = 1;
      setPressed(groupId, b.dataset.value);
      load();
    }));
  $('prev').addEventListener('click', () => { state.page--; load(); });
  // Rows per page (20 / 50 / 100): back to the first page with the new length.
  $('page-size').addEventListener('change', (e) => { state.pageSize = +e.target.value; state.page = 1; load(); });
  $('f-page-size')?.addEventListener('change', (e) => { fstate.pageSize = +e.target.value; fstate.page = 1; loadFinding(); });
  $('next').addEventListener('click', () => { state.page++; load(); });
  $('detail-body').addEventListener('click', (e) => { const tr = e.target.closest('tr[data-id]'); if (tr) showItem(tr.dataset.id); });
  $('detail-body').addEventListener('keydown', (e) => {
    const tr = e.target.closest('tr[data-id]');
    if (tr && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); showItem(tr.dataset.id); }
  });
  $('item-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', (e) => { if (e.target === dialog) dialog.close(); });

  // ------------------------------------------------------------------ "How is this score calculated?"
  const scoreDialog = $('score-dialog');
  if (scoreDialog && $('score-how')) {
    $('score-how').addEventListener('click', () => { if (!scoreDialog.open) scoreDialog.showModal(); });
    $('score-close').addEventListener('click', () => scoreDialog.close());
    scoreDialog.addEventListener('click', (e) => { if (e.target === scoreDialog) scoreDialog.close(); });
  }

  // Deep link: #Components opens that table.
  const initial = decodeURIComponent(location.hash.slice(1));
  if (kinds[initial]) open(initial);
  document.querySelectorAll('.tile').forEach(t => t.addEventListener('click', () => history.replaceState(null, '', '#' + t.dataset.kind)));
})();
