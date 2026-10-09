// Publication / folder picker (tree), shared by the schema, template and export steps.
(function () {
  const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

  async function api(url, options) {
    const res = await fetch(url, Object.assign({ headers: { 'RequestVerificationToken': token, 'Content-Type': 'application/json' } }, options));
    if (res.status === 401) { window.location = '/Account/Login?expired=true'; throw new Error('Session ended'); }
    const body = await res.json();
    if (!res.ok) throw new Error(body.error || res.statusText);
    return body;
  }
  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

  document.querySelectorAll('[data-picker]').forEach(picker => {
    const tree = picker.querySelector('.tree');
    const value = picker.querySelector('.picker__value');
    const selectedText = picker.querySelector('.picker__selected');
    const meta = picker.querySelector('.picker__meta');
    const newBox = picker.querySelector('.picker__new');
    const form = picker.closest('form');
    const sg = picker.dataset.mode === 'sg';
    let selected = null;

    function node(n, parentPath) {
      const li = el('li'); li.setAttribute('role', 'treeitem');
      const path = parentPath ? parentPath + ' / ' + n.title : n.title;
      Object.assign(li.dataset, { id: n.id, type: n.type, path });
      const row = el('div', 'tree__row');
      const toggle = el('button', 'tree__toggle', '▶');
      toggle.type = 'button'; toggle.setAttribute('aria-expanded', 'false'); toggle.setAttribute('aria-label', 'Expand ' + n.title);
      if (!n.hasChildren) toggle.disabled = true;
      const label = el('button', 'tree__label' + (n.type === 'Publication' ? ' is-pub' : ''));
      label.type = 'button';
      label.append(el('span', '', n.title));
      if (n.type === 'Folder' && n.schemaCount) label.append(el('span', 'count', n.schemaCount + ' schema' + (n.schemaCount === 1 ? '' : 's')));
      label.title = n.id;
      row.append(toggle, label); li.append(row);
      toggle.addEventListener('click', () => expand(li));
      label.addEventListener('click', () => n.type === 'Publication' ? expand(li) : select(li, n, label));
      if (n.type === 'StructureGroup') label.classList.add('is-sg');
      return li;
    }

    async function expand(li, reload) {
      const toggle = li.querySelector(':scope > .tree__row > .tree__toggle');
      let ul = li.querySelector(':scope > ul');
      if (ul && !reload) { ul.hidden = !ul.hidden; toggle.setAttribute('aria-expanded', String(!ul.hidden)); return; }
      if (ul) ul.remove();
      ul = el('ul'); ul.setAttribute('role', 'group'); ul.append(el('li', 'tree__empty', 'Loading…')); li.append(ul);
      try {
        const kids = await api('/api/tree/children?id=' + encodeURIComponent(li.dataset.id) + (sg ? '&mode=sg' : ''));
        ul.innerHTML = '';
        if (!kids.length) { ul.append(el('li', 'tree__empty', sg ? 'No sub-Structure Groups' : 'No sub-folders')); toggle.disabled = true; return; }
        kids.forEach(k => ul.append(node(k, li.dataset.path)));
        toggle.disabled = false; toggle.setAttribute('aria-expanded', 'true');
        if (li.dataset.type === 'Publication' && kids.length === 1) expand(ul.firstChild);
      } catch (e) { ul.innerHTML = ''; ul.append(el('li', 'tree__empty', 'Could not load: ' + e.message)); }
    }

    async function select(li, n, label) {
      picker.querySelectorAll('.tree__label[aria-selected=true]').forEach(x => x.setAttribute('aria-selected', 'false'));
      label.setAttribute('aria-selected', 'true');
      selected = { li, id: n.id };
      value.value = n.id;
      selectedText.textContent = li.dataset.path;
      meta.textContent = n.id;
      newBox.hidden = false;
      // A form with several pickers (e.g. folders + Structure Group) is ready only when every one has a choice.
      if (form) {
        const ready = Array.from(form.querySelectorAll('.picker__value')).every(v => v.value);
        form.querySelectorAll('button[type=submit]').forEach(b => b.disabled = !ready);
      }
      try {
        const f = await api((sg ? '/api/tree/structure-group?id=' : '/api/tree/folder?id=') + encodeURIComponent(n.id));
        meta.textContent = sg ? 'Structure Group in ' + f.publicationTitle + ', ' + n.id : f.schemaCount + ' schema' + (f.schemaCount === 1 ? '' : 's') + ' in this folder, ' + n.id;
        document.dispatchEvent(new CustomEvent('modelry:publication', { detail: { publicationId: f.publicationId } }));
      } catch (e) { meta.textContent = 'Could not read folder details: ' + e.message; }
    }

    newBox.querySelector('button').addEventListener('click', async () => {
      const input = newBox.querySelector('input');
      if (!selected || !input.value.trim()) { input.focus(); return; }
      const btn = newBox.querySelector('button'); btn.disabled = true;
      try {
        await api(sg ? '/api/tree/structure-groups' : '/api/tree/folders', { method: 'POST', body: JSON.stringify({ parentId: selected.id, title: input.value.trim() }) });
        input.value = '';
        await expand(selected.li, true);
      } catch (e) { alert((sg ? 'The Structure Group' : 'The folder') + ' was not created: ' + e.message); }
      finally { btn.disabled = false; }
    });

    (async () => {
      try {
        const pubs = await api('/api/tree/publications');
        tree.innerHTML = '';
        pubs.forEach(p => tree.append(node(p, '')));
        if (!pubs.length) tree.append(el('li', 'tree__empty', 'This account can\u2019t see any publications.'));
      } catch (e) { tree.innerHTML = ''; tree.append(el('li', 'tree__empty', 'Could not load publications: ' + e.message)); }
    })();
  });

  // Base template pickers (template step) follow the selected publication.
  let loadedFor = null;
  document.addEventListener('modelry:publication', async (ev) => {
    const pub = ev.detail.publicationId;
    if (pub === loadedFor) return;
    loadedFor = pub;
    for (const sel of document.querySelectorAll('.template-picker')) {
      sel.innerHTML = ''; sel.append(new Option('Loading templates…', ''));
      try {
        const items = await api('/api/tree/templates?publicationId=' + encodeURIComponent(pub) + '&kind=' + sel.dataset.kind);
        sel.innerHTML = '';
        sel.append(new Option(items.length ? 'Choose a base template' : 'No templates in this publication', ''));
        items.forEach(t => sel.append(new Option(t.title + ' (' + t.id + ')', t.id)));
        const guess = items.find(t => /dxa|base/i.test(t.title));
        if (guess) sel.value = guess.id;
      } catch (e) { sel.innerHTML = ''; sel.append(new Option('Could not load templates: ' + e.message, '')); }
    }
  });

  // Busy label on submit
  document.querySelectorAll('form[data-needs-folder]').forEach(f => f.addEventListener('submit', () => {
    const b = f.querySelector('button[type=submit][data-busy]');
    if (b && f.method.toLowerCase() === 'post') { setTimeout(() => { b.disabled = true; b.textContent = b.dataset.busy; }, 0); }
  }));
})();
