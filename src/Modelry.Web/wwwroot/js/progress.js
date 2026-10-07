// Polls /Import/Status and renders progress for schema and template creation.
(function () {
  const root = document.getElementById('job');
  const id = root.dataset.id;
  const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
  const $ = (x) => document.getElementById(x);
  const bar = $('bar');
  const isCheck = root.dataset.mode === 'Check';
  let timer = null, failures = 0;

  const fmt = (sec) => Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
  const esc = (s) => { const d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; };

  function render(s) {
    // Create: step 1 (reading the publication) has no measurable total. Check: steps are the measure.
    const preparing = !isCheck && s.step <= 1 && s.state === 'Running';
    bar.classList.toggle('indeterminate', preparing);
    bar.classList.toggle('failed', s.state === 'Failed' || s.state === 'PlanErrors');
    bar.classList.toggle('cancelled', s.state === 'Cancelled');
    bar.style.width = preparing ? '' : s.percent + '%';
    bar.parentElement.setAttribute('aria-valuenow', s.percent);
    $('pct').textContent = preparing ? '' : s.percent + '%';

    let label = 'Step ' + s.step + ' of ' + s.stepCount + ': ' + s.stepName;
    if (s.stepTotal > 0) label += ' (' + Math.min(s.stepDone + (s.currentItem ? 1 : 0), s.stepTotal) + ' of ' + s.stepTotal + ')';
    const done = {
      Checked: 'Check complete – opening the findings…',
      Completed: 'Finished – ' + s.created + ' created',
      Cancelled: 'Stopped – ' + s.created + ' created before stopping',
      Failed: 'Stopped by an error: ' + (s.error || 'see the log'),
      PlanErrors: 'The publication changed since the check – opening the updated findings…'
    }[s.state];
    $('step-label').textContent = done || label;
    $('item').textContent = s.state === 'Running' && s.currentItem ? s.currentItem : '';
    $('created').textContent = s.created;
    $('warnings').textContent = s.warnings;
    $('errors').textContent = s.errors;
    $('elapsed').textContent = fmt(s.elapsedSeconds);

    document.querySelectorAll('#steps li').forEach(li => {
      const n = +li.dataset.step;
      const isDone = n < s.step || s.state === 'Completed' || s.state === 'Checked';
      li.className = isDone ? 'done' : n === s.step ? (s.state === 'Running' ? 'active' : 'stopped') : 'pending';
      li.querySelector('.count').textContent = n === s.step && s.stepTotal > 0 && s.state === 'Running' ? Math.min(s.stepDone, s.stepTotal) + ' of ' + s.stepTotal : '';
    });

    const body = $('activity').querySelector('tbody');
    const levelLabel = (e) => e.step === 'Reading' ? 'Reading' : e.level === 'Info' ? 'Done' : esc(e.level);
    body.innerHTML = s.recent.length
      ? s.recent.map(e =>
          '<tr><td class="muted">' + esc(e.time) + '</td><td><span class="level level--' + esc(e.level) + '">' + levelLabel(e) +
          '</span></td><td>' + esc(e.item) + '</td><td>' + esc(e.message) + '</td></tr>').join('')
      : '<tr><td colspan="4" class="muted">Nothing yet – activity appears here as soon as Modelry starts reading the publication.</td></tr>';

    if (s.state !== 'Running') {
      clearInterval(timer);
      $('cancel').hidden = true;
      document.title = (s.state === 'Completed' || s.state === 'Checked' ? 'Done' : 'Stopped') + ' – Modelry';
      setTimeout(() => { window.location = '/Import/Job/' + id; }, s.state === 'Checked' ? 500 : s.state === 'Completed' ? 1200 : 2500);
    } else {
      document.title = s.percent + '% – Modelry';
    }
  }

  async function poll() {
    try {
      const res = await fetch('/Import/Status/' + id, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) throw new Error(res.status);
      failures = 0;
      render(await res.json());
    } catch (e) {
      if (++failures >= 5) { clearInterval(timer); $('step-label').textContent = 'Lost contact with the server. The work may still be running – refresh to reconnect.'; }
    }
  }

  $('cancel').addEventListener('click', async () => {
    if (!confirm($('cancel').dataset.confirm)) return;
    $('cancel').disabled = true;
    $('cancel').textContent = 'Stopping…';
    await fetch('/Import/Cancel/' + id, { method: 'POST', headers: { 'RequestVerificationToken': token } });
  });

  poll();
  timer = setInterval(poll, 1000);
})();
