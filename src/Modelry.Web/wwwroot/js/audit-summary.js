// Audit dashboard: AI executive summary. The server calls the AI service in the background (1.5–3 minutes);
// this script starts it, polls for the result and shows it – English always, plus a language tab when configured.
(function () {
  const box = document.getElementById('ai-summary');
  if (!box) return;
  const $ = (id) => document.getElementById(id);
  const esc = (s) => { const d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; };
  const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  const url = '/api/audit/' + box.dataset.job + '/summary';
  const enabled = box.dataset.enabled === 'true';
  const body = $('ai-body');
  const intro = body.innerHTML;
  const fmt = (sec) => Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
  let timer = null, tick = null, startedAt = 0, tab = 'en';

  function show(visible) { box.hidden = !visible; }

  function buttons(state, skipped) {
    $('ai-generate').hidden = !enabled || state === 'running' || state === 'completed';
    $('ai-regenerate').hidden = !enabled || state !== 'completed';
    $('ai-generate').textContent = state === 'failed' && !skipped ? 'Try again' : 'Generate AI summary';
  }

  function summaryHtml(e, text) {
    if (!e) return '<p class="ai-summary__text">' + esc(text) + '</p>';
    const status = (e.status || '').toLowerCase().replace(/\s+/g, '-');
    return (e.status ? '<span class="ai-status ai-status--' + esc(status) + '">' + esc(e.status) + '</span>' : '') +
      (e.headline ? '<p class="ai-summary__headline">' + esc(e.headline) + '</p>' : '') +
      (e.summary ? '<p class="ai-summary__text">' + esc(e.summary) + '</p>' : '') +
      (e.actions && e.actions.length ? '<h3 class="ai-summary__label">Next steps</h3><ol class="ai-actions">' +
        e.actions.map(a => '<li><span class="ai-prio ai-prio--' + esc((a.priority || '').toLowerCase()) + '">' + esc(a.priority || '') + '</span>' + esc(a.action) + '</li>').join('') +
        '</ol>' : '') +
      (e.decision ? '<div class="ai-decision"><h3 class="ai-summary__label">Decision for you</h3><p>' + esc(e.decision) + '</p></div>' : '');
  }

  function renderCompleted(s) {
    const r = s.summary;
    const hasTr = r.hasTranslation;
    const tabs = hasTr
      ? '<div class="ai-tabs" role="tablist" aria-label="Language">' +
          '<button type="button" role="tab" id="ai-tab-en" aria-selected="' + (tab === 'en') + '" aria-controls="ai-panel">English</button>' +
          '<button type="button" role="tab" id="ai-tab-tr" aria-selected="' + (tab !== 'en') + '" aria-controls="ai-panel" lang="' + esc(r.language) + '">' + esc(r.languageName) + '</button>' +
        '</div>'
      : '';
    const translated = hasTr && tab !== 'en';
    const content = translated ? summaryHtml(r.translated, r.translatedText) : summaryHtml(r.english, r.englishText);
    const when = new Date(r.createdUtc);
    body.innerHTML = tabs +
      '<div id="ai-panel" role="' + (hasTr ? 'tabpanel' : 'region') + '"' + (translated ? ' lang="' + esc(r.language) + '" dir="' + (/^(ar|he|fa|ur|ps|yi)(-|$)/i.test(r.language) ? 'rtl' : 'auto') + '"' : '') + '>' + content + '</div>' +
      '<p class="muted small ai-summary__meta">AI-generated from this audit\'s scores and counts' +
        (r.score ? ' (score ' + esc(r.score) + ')' : '') + ' on ' + esc(when.toLocaleString()) + (r.createdBy ? ' by ' + esc(r.createdBy) : '') +
        ' · ' + esc(r.words) + ' words' + (r.trimmed ? ' (shortened to the word limit)' : '') + '. Check it before sharing.</p>';
    if (hasTr) {
      $('ai-tab-en').addEventListener('click', () => { tab = 'en'; renderCompleted(s); $('ai-tab-en').focus(); });
      $('ai-tab-tr').addEventListener('click', () => { tab = 'tr'; renderCompleted(s); $('ai-tab-tr').focus(); });
      body.querySelector('.ai-tabs').addEventListener('keydown', (ev) => {
        if (ev.key === 'ArrowRight' || ev.key === 'ArrowLeft') { tab = tab === 'en' ? 'tr' : 'en'; renderCompleted(s); $(tab === 'en' ? 'ai-tab-en' : 'ai-tab-tr').focus(); }
      });
    }
  }

  function render(s) {
    clearInterval(tick);
    buttons(s.state, s.skipped);
    if (s.state === 'none') { show(enabled); body.innerHTML = intro; return; }
    show(true);
    if (s.state === 'running') {
      startedAt = Date.now() - (s.elapsed || 0) * 1000;
      const draw = () => {
        const sec = Math.round((Date.now() - startedAt) / 1000);
        body.innerHTML = '<div class="ai-wait"><span class="ai-wait__bar" aria-hidden="true"><span></span></span>' +
          '<p><b>Writing the summary…</b> <span class="muted">' + fmt(sec) + ' – this usually takes 2–3 minutes. You can keep using the dashboard.</span></p></div>';
      };
      draw();
      tick = setInterval(draw, 1000);
      return;
    }
    if (s.state === 'completed') { renderCompleted(s); return; }
    body.innerHTML = s.skipped ? '<p class="muted ai-summary__intro">' + esc(s.error) + '</p>'
      : '<div class="alert alert--' + (s.rateLimited ? 'warn' : 'error') + '" role="alert"><p>' + esc(s.error || 'The summary could not be created.') + '</p></div>';
  }

  async function poll() {
    try {
      const r = await fetch(url, { headers: { Accept: 'application/json' } });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const s = await r.json();
      render(s);
      if (s.state !== 'running') { clearInterval(timer); timer = null; }
    } catch (e) {
      /* keep polling: a single failed status call is not a failed summary */
    }
  }

  async function start(regenerate) {
    if (regenerate && !confirm('Generate a new summary? Each summary uses one of this server\'s limited AI calls per hour.')) return;
    $('ai-generate').disabled = $('ai-regenerate').disabled = true;
    try {
      const r = await fetch(url + (regenerate ? '?regenerate=true' : ''), { method: 'POST', headers: { RequestVerificationToken: token(), Accept: 'application/json' } });
      const s = await r.json().catch(() => ({ state: 'failed', error: 'The summary could not be started (HTTP ' + r.status + ').' }));
      if (!r.ok && !s.state) { s.state = 'failed'; }
      render(s);
      if (s.state === 'running' && !timer) timer = setInterval(poll, 5000);
    } finally {
      $('ai-generate').disabled = $('ai-regenerate').disabled = false;
    }
  }

  $('ai-generate').addEventListener('click', () => start(false));
  $('ai-regenerate').addEventListener('click', () => start(true));
  poll().then(() => { if (!box.hidden && body.querySelector('.ai-wait') && !timer) timer = setInterval(poll, 5000); });
})();
