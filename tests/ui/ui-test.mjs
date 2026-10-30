// Test UI de bout en bout : charge la page du plugin dans jsdom comme le fait Plugin Pages
// (fragment HTML injecté), avec un faux ApiClient branché sur le serveur de test (mode "serve").
// Usage : node ui-test.mjs   (lance lui-même le serveur de tests/Watchlists.ApiTests)
import { spawn } from 'node:child_process';
import { JSDOM } from 'jsdom';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const proc = spawn('dotnet', ['run', '-c', 'Release', '-p:UseStubs=true', '--no-build', '--', 'serve'], { cwd: path.join(here, '../Watchlists.ApiTests') });
const info = await new Promise((resolve, reject) => {
    let buf = '';
    proc.stdout.on('data', d => { buf += d; const m = buf.match(/SERVE (.*)\n/); if (m) resolve(JSON.parse(m[1])); });
    proc.on('exit', c => reject(new Error('serveur arrêté ' + c)));
    setTimeout(() => reject(new Error('timeout serveur')), 60000);
});
const U = Object.fromEntries(info.users.map(u => [u.name, u]));

const TEST_USER_DATA = { Dune: { Played: true, IsFavorite: true }, Severance: { IsFavorite: true } };
let pass = 0, failures = 0;
const check = (ok, label) => { if (ok) pass++; else { failures++; console.log('FAIL: ' + label); } };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, label, timeout = 5000) {
    const t0 = Date.now();
    while (Date.now() - t0 < timeout) { try { const v = fn(); if (v) return v; } catch { } await sleep(25); }
    failures++; console.log('FAIL (timeout): ' + label); return null;
}

async function openPage(user, isAdmin = false) {
    const dom = new JSDOM('<!doctype html><body><div class="userPluginSettingsContainer"></div></body>', { runScripts: 'dangerously', url: 'http://localhost/web/#/userpluginsettings.html' });
    const w = dom.window;
    const errors = [];
    w.addEventListener('error', e => errors.push(e.message));
    w.confirm = () => true;
    w.prompt = () => 'note UI';
    const headers = { 'X-Test-User': user.id, ...(isAdmin ? { 'X-Test-Admin': '1' } : {}) };
    w.ApiClient = {
        getCurrentUserId: () => user.id,
        serverId: () => 'srv',
        getUrl(p, params) {
            const u = new URL(info.baseUrl + '/' + p.replace(/^\//, ''));
            if (params) Object.entries(params).forEach(([k, v]) => u.searchParams.set(k, v));
            return u.toString();
        },
        async ajax(o) {
            const h = { ...headers }; if (o.contentType) h['Content-Type'] = o.contentType;
            const res = await fetch(o.url, { method: o.type, headers: h, body: o.data });
            if (res.status >= 400) throw res;
            return o.dataType === 'json' ? res.json() : res;
        },
        async getItems(uid, q) {
            if (q.Ids) {
                // Données utilisateur (vu / favori) : identiques pour tous les utilisateurs de test.
                const ids = q.Ids.split(',').map(id => id.replace(/-/g, '').toLowerCase());
                return { Items: info.items.filter(i => ids.includes(i.Id.toLowerCase())).map(i => ({ Id: i.Id, UserData: TEST_USER_DATA[i.Name] || {} })) };
            }
            const term = q.SearchTerm.toLowerCase(); const types = q.IncludeItemTypes.split(',');
            return { Items: info.items.filter(i => i.Name.toLowerCase().includes(term) && types.includes(i.Type)).map(i => ({ ...i, ImageTags: {} })) };
        },
        async getCurrentUser() { return { Policy: { IsAdministrator: isAdmin } }; }
    };
    // Reproduit Plugin Pages : GET du fragment puis createContextualFragment + append.
    const html = await (await fetch(info.baseUrl + '/Watchlists/Web/watchlists.html', { headers })).text();
    const container = w.document.querySelector('.userPluginSettingsContainer');
    container.append(w.document.createRange().createContextualFragment(html));
    // Les navigateurs exécutent les <script> d'un fragment contextuel à l'insertion ; jsdom non :
    // on les ré-insère pour reproduire le comportement réel.
    container.querySelectorAll('script').forEach(old => {
        const s = w.document.createElement('script');
        s.textContent = old.textContent;
        old.replaceWith(s);
    });
    const $ = s => w.document.querySelector(s);
    const $$ = s => [...w.document.querySelectorAll(s)];
    const click = el => el.dispatchEvent(new w.MouseEvent('click', { bubbles: true }));
    const byText = (sel, txt) => $$(sel).find(e => e.textContent.includes(txt));
    return { w, $, $$, click, byText, errors };
}

try {
    // ---------------- alice
    const a = await openPage(U.alice);
    await until(() => a.$('.wl-empty-state'), 'accueil vide rendu');
    check(a.$$('.wl-tab').length === 4, '4 onglets');

    a.click(a.$('[data-act="new"]'));
    const form = await until(() => a.$('form[data-form="create"]'), 'formulaire de création');
    form.elements.name.value = 'Soirées <b>ciné</b>';
    form.elements.visibility.value = 'Public';
    form.dispatchEvent(new a.w.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => a.$('.wl-detail-head h1')?.textContent === 'Soirées <b>ciné</b>', 'détail ouvert, nom échappé');
    check(!a.$('.wl-detail-head h1 b'), 'pas d\'injection HTML dans le titre');
    check(!!a.$('[data-search]'), 'recherche visible pour l\'owner');

    const search = a.$('[data-search]');
    search.value = 'du'; search.dispatchEvent(new a.w.Event('input', { bubbles: true }));
    const res = await until(() => a.$('[data-add]'), 'résultats de recherche');
    check(a.$$('[data-add^="i"]').length === 1 && res.textContent.includes('Dune'), 'Dune trouvé sur le serveur (épisodes exclus)');
    const tmdbRows = a.$$('[data-add^="t"]');
    check(tmdbRows.length === 1 && tmdbRows[0].textContent.includes('Deuxième partie') && tmdbRows[0].textContent.includes('Pas sur le serveur'), 'TMDB : seulement le titre absent (Dune présent dédoublonné)');
    check(a.$$('.wl-section').map(x => x.textContent).join('|') === 'Sur le serveur|Pas sur le serveur (TMDB)', 'sections de recherche');
    a.click(res);
    await until(() => a.$$('.wl-item').length === 1, 'Dune ajouté');

    const s2 = a.$('[data-search]');
    s2.value = 'sev'; s2.dispatchEvent(new a.w.Event('input', { bubbles: true }));
    a.click(await until(() => a.$('[data-add]'), 'résultat Severance'));
    await until(() => a.$$('.wl-item').length === 2, 'Severance ajouté');
    check(a.$$('.wl-item-title')[1].textContent.includes('Série'), 'badge Série');
    await until(() => a.$$('.wl-mark.fav').length === 2, 'badges favori (Dune, Severance)');
    const duneItem = a.$$('.wl-item').find(e => e.textContent.includes('Dune'));
    const sevItem = a.$$('.wl-item').find(e => e.textContent.includes('Severance'));
    check(!!duneItem?.querySelector('.wl-mark.played[title="Déjà vu"]') && !sevItem?.querySelector('.wl-mark.played'), 'badge « Déjà vu » sur Dune uniquement');

    a.click(a.$('[data-act="down"]'));
    await until(() => a.$$('.wl-item-title')[0]?.textContent.includes('Severance'), 'réordonnancement');
    a.click(a.$$('[data-act="note"]')[0]);
    await until(() => a.$('.wl-note')?.textContent === 'note UI', 'note ajoutée');

    a.click(a.$('[data-act="settings"]'));
    const inv = await until(() => a.$('form[data-form="invite"]'), 'panneau paramètres + invitation');
    check(!inv.querySelector('[name="force"]'), 'pas d\'option admin pour un non-admin');
    inv.elements.userId.value = [...inv.elements.userId.options].find(o => o.textContent === 'bob').value;
    inv.dispatchEvent(new a.w.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => a.$$('.wl-chip').some(c => c.textContent.includes('bob') && c.textContent.includes('invité')), 'bob invité');
    check(!!a.$('form[data-form="update"]'), 'paramètres restent ouverts après invitation');

    a.click(a.$('[data-act="back"]'));
    await until(() => a.$('.wl-card'), 'carte sur l\'accueil');
    check(a.$('.wl-card').textContent.includes('Publique') && a.$('.wl-card').textContent.includes('2 titres'), 'carte : badges et compteur');
    check(a.$$('.wl-card img').length === 0 || a.$$('.wl-card img').every(i => !i.getAttribute('onerror')?.includes('alert')), 'aucune image injectée');
    check(a.errors.length === 0, 'aucune erreur JS côté alice : ' + a.errors.join(' | '));

    // ---------------- bob
    const b = await openPage(U.bob);
    await until(() => b.$('[data-tab="invitations"] .wl-count')?.textContent === '1', 'badge invitations = 1');
    b.click(b.$('[data-tab="public"]'));
    await until(() => b.$('.wl-card'), 'bob voit la liste publique dans Découvrir');
    b.click(b.$('[data-tab="invitations"]'));
    b.click(await until(() => b.$('.wl-card'), 'carte invitation'));
    b.click(await until(() => b.$('[data-act="accept"]'), 'bannière invitation'));
    await until(() => b.$('[data-search]') && !b.$('[data-act="accept"]'), 'bob accepte et peut éditer');
    check(!!b.$('[data-act="leave"]') && !b.$('[data-act="settings"]'), 'bob : quitter oui, paramètres non');
    b.click(b.$$('[data-act="remove"]')[0]);
    await until(() => b.$$('.wl-item').length === 1, 'bob retire un item');
    check(b.errors.length === 0, 'aucune erreur JS côté bob : ' + b.errors.join(' | '));

    // ---------------- carol (non membre, liste publique)
    const c = await openPage(U.carol);
    c.click(c.$('[data-tab="public"]'));
    c.click(await until(() => c.$('.wl-card'), 'carol voit la liste publique'));
    await until(() => c.$('[data-act="follow"]'), 'bouton suivre');
    check(!c.$('[data-search]') && !c.$('[data-act="remove"]'), 'carol en lecture seule');
    c.click(c.$('[data-act="follow"]'));
    await until(() => c.$('[data-act="unfollow"]'), 'carol suit');

    // ---------------- erreur API affichée
    const adm = await openPage(U.admin, true);
    adm.click(adm.$('[data-act="new"]'));
    const f2 = await until(() => adm.$('form[data-form="create"]'), 'form admin');
    f2.elements.name.value = 'Admin';
    f2.dispatchEvent(new adm.w.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => adm.$('[data-act="settings"]'), 'liste admin');
    adm.click(adm.$('[data-act="settings"]'));
    const f3 = await until(() => adm.$('form[data-form="invite"] [name="force"]'), 'option ajout direct pour admin');
    f3.checked = true; f3.form.elements.userId.value = [...f3.form.elements.userId.options].find(o => o.textContent === 'carol').value;
    f3.form.dispatchEvent(new adm.w.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => adm.$$('.wl-chip').some(ch => ch.textContent.includes('carol') && !ch.textContent.includes('invité')), 'carol ajoutée directement');
    const upd = adm.$('form[data-form="update"]');
    upd.elements.name.value = '   ';
    upd.elements.name.removeAttribute('required');
    upd.dispatchEvent(new adm.w.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => adm.$('.wl-toast.err')?.textContent.includes('Le nom est obligatoire'), 'message d\'erreur API affiché');

    // ---------------- vue admin : toutes les listes, groupées par propriétaire
    await fetch(info.baseUrl + '/Watchlists', { method: 'POST', headers: { 'X-Test-User': U.carol.id, 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Carol secret' }) });
    const adm2 = await openPage(U.admin, true);
    adm2.click(await until(() => adm2.$('[data-tab="admin"]'), 'onglet admin visible pour un admin'));
    await until(() => adm2.$('.wl-owner'), 'vue admin rendue');
    const owners = adm2.$$('.wl-owner').map(x => x.firstChild.textContent);
    check(owners.includes('carol') && owners.includes('alice') && owners.includes('admin'), 'vue admin : un groupe par propriétaire (' + owners.join(', ') + ')');
    const secret = adm2.byText('.wl-card', 'Carol secret');
    check(!!secret && secret.textContent.includes('Privée'), 'vue admin : liste privée de carol visible');
    check(adm2.byText('.wl-card', 'Admin')?.textContent.includes('avec carol'), 'vue admin : membres affichés');
    adm2.click(secret);
    await until(() => adm2.$('.wl-detail-head h1')?.textContent === 'Carol secret', 'vue admin : détail d\'une liste privée');
    check(!!adm2.$('.wl-badge.admin') && !adm2.$('[data-search]') && !adm2.$('[data-act="settings"]'), 'vue admin : lecture seule');
    adm2.click(adm2.$('[data-act="back"]'));
    await until(() => adm2.$('.wl-owner'), 'retour sur la vue admin');
    check(adm2.errors.length === 0, 'aucune erreur JS côté admin : ' + adm2.errors.join(' | '));

    // ---------------- bouton d'en-tête (header.js) : carol n'a pas d'invitation, bob en reçoit une
    const r4 = await fetch(info.baseUrl + '/Watchlists', { method: 'POST', headers: { 'X-Test-User': U.carol.id, 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Pour bob' }) }).then(r => r.json());
    await fetch(info.baseUrl + '/Watchlists/' + r4.id + '/Members', { method: 'POST', headers: { 'X-Test-User': U.carol.id, 'Content-Type': 'application/json' }, body: JSON.stringify({ userId: U.bob.id }) });
    const headerJs = await (await fetch(info.baseUrl + '/Watchlists/Web/header.js')).text();
    for (const [who, expected] of [['carol', null], ['bob', '1']]) {
        const hd = new JSDOM('<!doctype html><head></head><body><div class="skinHeader"><div class="headerRight">'
            + '<button class="headerSyncButton"></button><button class="jeCalendar"></button><button class="headerCastButton"></button>'
            + '<button class="headerSearchButton"></button><button class="headerUserButton"></button></div></div></body>',
            { runScripts: 'dangerously', url: 'http://localhost/web/#/home' });
        const hw = hd.window; const errs = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        hw.ApiClient = {
            getCurrentUserId: () => U[who].id,
            getUrl: p => info.baseUrl + '/' + p,
            getJSON: url => fetch(url, { headers: { 'X-Test-User': U[who].id } }).then(r => r.json())
        };
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        const btn = await until(() => hw.document.querySelector('.watchlistsHeaderButton'), who + ' : bouton inséré');
        check(btn?.nextElementSibling?.classList.contains('headerCastButton'), who + ' : bouton placé avant cast');
        check(btn?.querySelector('.material-icons')?.textContent === 'playlist_add_check', who + ' : icône (ligature)');
        if (expected) await until(() => btn.querySelector('.wl-dot')?.textContent === expected, who + ' : pastille invitations');
        else { await sleep(300); check(!btn.querySelector('.wl-dot'), who + ' : pas de pastille'); }
        // Le header est re-rendu par Jellyfin : le bouton doit revenir, sans doublon.
        btn.remove();
        await until(() => hw.document.querySelectorAll('.watchlistsHeaderButton').length === 1, who + ' : bouton réinséré après re-rendu');
        hw.document.querySelector('.watchlistsHeaderButton').click();
        check(hw.location.hash === '#/userpluginsettings.html?pageUrl=/Watchlists/Web/watchlists.html', who + ' : clic -> page Watchlists');
        check(errs.length === 0, who + ' : aucune erreur JS header : ' + errs.join(' | '));
    }

    // ---------------- Jellyfin 12 : layout MUI, ancien .headerRight masqué
    {
        const hd = new JSDOM('<!doctype html><head></head><body>'
            + '<div style="display:none"><div class="skinHeader"><div class="headerRight"><button class="headerCastButton"></button><button class="headerUserButton"></button></div></div></div>'
            + '<header class="MuiAppBar-root"><div class="MuiToolbar-root"><button class="burger"></button><div class="mui-tray"><button class="je-req"></button><button class="mui-search"></button></div>'
            + '<div class="user-box"><button aria-controls="app-user-menu"></button></div></div></header></body>',
            { runScripts: 'dangerously', url: 'http://localhost/web/#/home' });
        const hw = hd.window; const errs = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        hw.ApiClient = { getCurrentUserId: () => U.carol.id, getUrl: p => info.baseUrl + '/' + p, getJSON: url => fetch(url, { headers: { 'X-Test-User': U.carol.id } }).then(r => r.json()) };
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        const btn = await until(() => hw.document.querySelector('.watchlistsHeaderButton'), 'MUI : bouton inséré');
        check(btn?.parentElement?.classList.contains('mui-tray') && btn === btn.parentElement.firstElementChild, 'MUI : bouton dans la zone visible de la Toolbar, en tête');
        check(!hw.document.querySelector('.skinHeader .watchlistsHeaderButton'), 'MUI : rien dans l\'en-tête legacy masqué');
        // React remonte la zone de boutons : le bouton doit revenir dans la nouvelle zone
        const tray = hw.document.querySelector('.mui-tray'); const fresh = hw.document.createElement('div'); fresh.className = 'mui-tray2'; fresh.innerHTML = '<button></button>';
        tray.replaceWith(fresh);
        await until(() => hw.document.querySelector('.mui-tray2 .watchlistsHeaderButton'), 'MUI : bouton réinséré après remontage');
        check(hw.document.querySelectorAll('.watchlistsHeaderButton').length === 1, 'MUI : pas de doublon');
        check(errs.length === 0, 'MUI : aucune erreur JS : ' + errs.join(' | '));
    }

    // ---------------- suppression depuis Jellyfin : avertissement si le titre est dans une watchlist (header.js)
    {
        const hh = { 'X-Test-User': U.carol.id };
        const post = (url, body) => fetch(info.baseUrl + url, { method: 'POST', headers: { ...hh, 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(r => r.json());
        const keep = info.items.find(i => i.Name === 'Severance');
        const keepList = await post('/Watchlists', { name: 'Carol à garder' });
        await post('/Watchlists/' + keepList.id + '/Items', { itemId: keep.Id });
        const norm = id => id.replace(/-/g, '').toLowerCase();
        const hits = await fetch(info.baseUrl + '/Watchlists/DeleteCheck?ids=' + info.items.map(i => i.Id).join(','), { headers: hh }).then(r => r.json());
        const free = info.items.find(i => !hits.some(h => norm(h.itemId) === norm(i.Id)));
        check(!!free, 'suppression : un titre hors watchlist existe pour le test');
        const hd = new JSDOM('<!doctype html><head></head><body></body>', { runScripts: 'dangerously', url: 'http://localhost/web/#/details?id=' + keep.Id });
        const hw = hd.window; const errs = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        const deleted = [];
        let failCheck = false;
        // Comme jellyfin-apiclient : deleteItem est une méthode de classe (header.js l'enveloppe sur le prototype).
        class FakeApiClient {
            getCurrentUserId() { return U.carol.id; }
            getUrl(p) { return info.baseUrl + '/' + p; }
            getJSON(url) { return fetch(url, { headers: hh }).then(r => r.json()); }
            async ajax(o) {
                if (failCheck && o.url.includes('DeleteCheck')) throw new Error('réseau');
                const res = await fetch(o.url, { method: o.type, headers: hh });
                if (res.status >= 400) throw res;
                return res.json();
            }
            deleteItem(id) { deleted.push(id); return Promise.resolve(); }
        }
        hw.ApiClient = new FakeApiClient();
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        const $ = q => hw.document.querySelector(q);
        await until(() => FakeApiClient.prototype.deleteItem.wlWrapped, 'suppression : deleteItem enveloppé');

        await hw.ApiClient.deleteItem(free.Id);
        check(deleted.includes(free.Id) && !$('.wl-del-backdrop'), 'suppression : titre hors watchlist supprimé sans question');

        deleted.length = 0;
        let settled = false;
        hw.ApiClient.deleteItem(keep.Id).then(() => { settled = true; });
        const dlg = await until(() => $('.wl-del-backdrop'), 'suppression : avertissement affiché');
        check(dlg?.textContent.includes('« Severance »') && dlg.textContent.includes('« Carol à garder »'), 'suppression : titre et liste nommés');
        check(hw.document.activeElement?.getAttribute('data-act') === 'cancel', 'suppression : focus sur Annuler');
        check(deleted.length === 0, 'suppression : rien supprimé avant la réponse');
        $('.wl-del-backdrop [data-act="cancel"]').click();
        await sleep(100);
        check(!settled && deleted.length === 0 && !$('.wl-del-backdrop'), 'suppression : Annuler -> rien supprimé, pas d\'erreur');

        hw.ApiClient.deleteItem(keep.Id);
        await until(() => $('.wl-del-backdrop'), 'suppression : avertissement (Échap)');
        hw.document.dispatchEvent(new hw.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await sleep(100);
        check(deleted.length === 0 && !$('.wl-del-backdrop'), 'suppression : Échap annule');

        // Sélection multiple : appels dans le même tick -> une seule vérification, une seule question.
        const both = Promise.all([hw.ApiClient.deleteItem(keep.Id), hw.ApiClient.deleteItem(free.Id)]);
        await until(() => $('.wl-del-backdrop'), 'suppression multiple : avertissement');
        check(hw.document.querySelectorAll('.wl-del-backdrop').length === 1 && deleted.length === 0, 'suppression multiple : une seule question, rien supprimé avant');
        $('.wl-del-backdrop [data-act="delete"]').click();
        await both;
        check(deleted.includes(keep.Id) && deleted.includes(free.Id), 'suppression multiple : confirmer supprime tout');

        deleted.length = 0;
        failCheck = true;
        await hw.ApiClient.deleteItem(keep.Id);
        check(deleted.includes(keep.Id) && !$('.wl-del-backdrop'), 'suppression : vérification en échec -> suppression normale');
        check(errs.length === 0, 'suppression : aucune erreur JS : ' + errs.join(' | '));
        await fetch(info.baseUrl + '/Watchlists/' + keepList.id, { method: 'DELETE', headers: hh });
    }

    // ---------------- fiche : ajout -> proposition de liste commune (header.js)
    {
        const post = (who, url, body) => fetch(info.baseUrl + url, { method: 'POST', headers: { 'X-Test-User': U[who].id, 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(r => r.json());
        const sev = info.items.find(i => i.Name === 'Severance');
        const bobList = await post('bob', '/Watchlists', { name: 'Bob séries' });
        await post('bob', '/Watchlists/' + bobList.id + '/Items', { itemId: sev.Id });
        const carolList = await post('carol', '/Watchlists', { name: 'Carol séries' });
        const hd = new JSDOM('<!doctype html><head></head><body><div class="mainDetailButtons"><button class="btnMoreCommands"></button></div></body>',
            { runScripts: 'dangerously', url: 'http://localhost/web/#/details?id=' + sev.Id });
        const hw = hd.window; const errs = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        const hh = { 'X-Test-User': U.carol.id };
        hw.ApiClient = {
            getCurrentUserId: () => U.carol.id,
            getUrl: p => info.baseUrl + '/' + p,
            getJSON: url => fetch(url, { headers: hh }).then(r => r.json()),
            getItem: async () => ({ Type: 'Series' }),
            async ajax(o) {
                const h = { ...hh }; if (o.contentType) h['Content-Type'] = o.contentType;
                const res = await fetch(o.url, { method: o.type, headers: h, body: o.data });
                if (res.status >= 400) throw res;
                return res.json();
            }
        };
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        const $ = q => hw.document.querySelector(q);
        const byText = (q, t) => [...hw.document.querySelectorAll(q)].find(e => e.textContent.includes(t));
        const dbtn = await until(() => { const b = $('.watchlistsDetailButton'); return b && !b.disabled && b; }, 'match : bouton de fiche prêt');
        dbtn.click();
        const carolBtn = await until(() => byText('.wl-detail-menu button', 'Carol séries'), 'match : menu avec « Carol séries »');
        let groupEl = carolBtn?.previousElementSibling;
        while (groupEl && !groupEl.classList.contains('wl-group')) groupEl = groupEl.previousElementSibling;
        check(groupEl?.textContent === 'lockPrivées', 'menu : liste privée rangée sous « Privées »');
        carolBtn?.click();
        const row = await until(() => byText('.wl-match-row', 'bob a aussi ce titre'), 'match : proposition affichée pour bob');
        check(!!$('.wl-match.wl-anchored') && !$('.wl-detail-menu') && $('.wl-match').style.top.endsWith('px') && !$('.wl-match').classList.contains('wl-above'), 'match : proposition ancrée sous le bouton, à la place du menu');
        // carol a déjà « Pour bob » (bob invité) : c'est la liste commune proposée.
        const addBtn = row && byText('.wl-match-row button', 'Pour bob');
        check(!!addBtn && addBtn.classList.contains('wl-primary'), 'match : liste commune existante proposée');
        addBtn?.click();
        await until(() => byText('.wl-toast', 'Ajouté à « Pour bob »'), 'match : toast après ajout à la liste commune');
        check(!$('.wl-match'), 'match : proposition fermée après action');
        // Bouton en bas de l'écran : ouverture au-dessus.
        {
            const realRect = dbtn.getBoundingClientRect;
            dbtn.getBoundingClientRect = () => ({ left: 100, width: 40, top: hw.innerHeight - 50, bottom: hw.innerHeight - 10, right: 140, height: 40 });
            Object.defineProperty(hw.HTMLElement.prototype, 'offsetHeight', { configurable: true, get() { return this.classList.contains('wl-match') ? 200 : 0; } });
            const ent = (await fetch(info.baseUrl + '/Watchlists/' + carolList.id, { headers: hh }).then(r => r.json())).items[0].entryId;
            hw.__watchlistsShowMatches({ addedEntryId: ent, matches: [{ user: { id: U.alice.id, name: 'alice' }, lists: [] }] }, carolList.id, null, dbtn);
            const up = await until(() => $('.wl-match.wl-anchored'), 'match : proposition ancrée (bas d\'écran)');
            check(up?.classList.contains('wl-above') && up.style.top === 'auto' && up.style.bottom === '60px', 'match : ouverte au-dessus du bouton faute de place en dessous');
            hw.document.body.dispatchEvent(new hw.MouseEvent('mousedown', { bubbles: true }));
            check(!$('.wl-match'), 'match : clic à côté ferme la proposition ancrée');
            delete hw.HTMLElement.prototype.offsetHeight;
            dbtn.getBoundingClientRect = realRect;
        }
        const pourBob = (await fetch(info.baseUrl + '/Watchlists', { headers: hh }).then(r => r.json())).find(l => l.name === 'Pour bob');
        const detail = await fetch(info.baseUrl + '/Watchlists/' + pourBob.id, { headers: hh }).then(r => r.json());
        check(detail.items.some(i => i.title === 'Severance'), 'match : titre copié dans « Pour bob »');
        // Sans liste commune : bouton de création.
        const entry = (await fetch(info.baseUrl + '/Watchlists/' + carolList.id, { headers: hh }).then(r => r.json())).items[0].entryId;
        hw.__watchlistsShowMatches({ addedEntryId: entry, matches: [{ user: { id: U.alice.id, name: 'alice' }, lists: [] }] }, carolList.id);
        (await until(() => byText('.wl-match-row button', 'Créer avec alice'), 'match : bouton de création')).click();
        await until(() => byText('.wl-toast', '« carol & alice » créée'), 'match : liste commune créée');
        hw.__watchlistsShowMatches({ addedEntryId: entry, matches: [{ user: { id: U.alice.id, name: 'alice' }, lists: [] }] }, carolList.id);
        (await until(() => byText('.wl-match-row button', 'Ignorer'), 'match : bouton Ignorer')).click();
        check(!$('.wl-match'), 'match : Ignorer ferme la proposition');
        check(errs.length === 0, 'match : aucune erreur JS : ' + errs.join(' | '));
    }

    // ---------------- filtre « Watchlist » du panneau Filtres des bibliothèques (header.js)
    {
        const LIB = 'abcdef0123456789abcdef0123456789';
        const sev = info.items.find(i => i.Name === 'Severance');
        const hh = { 'X-Test-User': U.carol.id };
        const hd = new JSDOM('<!doctype html><head></head><body></body>', { runScripts: 'dangerously', url: 'http://localhost/web/#/tv?topParentId=' + LIB + '&collectionType=tvshows' });
        const hw = hd.window; const errs = []; const queries = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        hw.ApiClient = {
            getCurrentUserId: () => U.carol.id,
            getUrl: p => info.baseUrl + '/' + p,
            getJSON: url => fetch(url, { headers: hh }).then(r => r.json()),
            getItems(uid, q) { queries.push(q); return Promise.resolve({ Items: [], TotalRecordCount: 0 }); },
            async ajax(o) {
                const res = await fetch(o.url, { method: o.type, headers: hh, body: o.data });
                if (res.status >= 400) throw res;
                return res.json();
            }
        };
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        // Panneau tel que le crée Jellyfin (filterdialog) : la page relit la bibliothèque à chaque changement.
        const libQuery = { IncludeItemTypes: 'Series', Recursive: true, ParentId: LIB, StartIndex: 0 };
        const reload = () => hw.ApiClient.getItems(U.carol.id, libQuery);
        await sleep(50);
        const dlg = hw.document.createElement('div');
        dlg.className = 'filterDialog';
        dlg.innerHTML = '<div class="filterDialogContent"><div class="collapseContent"><div class="checkboxList">'
            + '<label><input type="checkbox" class="chkStandardFilter" data-filter="IsPlayed"><span>Vus</span></label>'
            + '<label><input type="checkbox" class="chkStandardFilter chkFavorite" data-filter="IsFavorite"><span>Favoris</span></label>'
            + '</div></div></div><button class="resetFilters"></button>';
        dlg.querySelectorAll('.chkStandardFilter').forEach(c => c.addEventListener('change', reload));
        dlg.querySelector('.resetFilters').addEventListener('click', () => { dlg.querySelectorAll('input:checked').forEach(c => { c.checked = false; }); reload(); });
        const btn = name => hw.document.querySelector('.wl-chk-watchlist[data-name="' + name + '"]');
        const lastQ = () => queries[queries.length - 1];
        const reloadLast = async () => { await sleep(50); await reload(); return lastQ(); };
        // Liste « Zz A » = Dune + Severance ; « Carol séries » = Severance.
        const dune = info.items.find(i => i.Name === 'Dune');
        const zzA = await (await fetch(info.baseUrl + '/Watchlists', { method: 'POST', headers: { ...hh, 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Zz A' }) })).json();
        for (const it of [dune, sev]) await fetch(info.baseUrl + '/Watchlists/' + zzA.id + '/Items', { method: 'POST', headers: { ...hh, 'Content-Type': 'application/json' }, body: JSON.stringify({ itemId: it.Id }) });
        hw.document.body.appendChild(dlg);
        const $ = q => hw.document.querySelector(q);
        const chk = await until(() => btn('Carol séries'), 'filtre : case « Carol séries »');
        check($('.wl-library-filter')?.previousElementSibling === dlg.querySelector('.checkboxList') && $('.wl-library-filter h3')?.textContent === 'Watchlist', 'filtre : section « Watchlist » sous Favoris');
        check($('.wl-fhelp')?.textContent.includes('2 clics : pas dans la liste'), 'filtre : aide des trois états');
        check(hw.ApiClient.getItems.wlWrapped === true, 'filtre : getItems enveloppé');
        chk.click();
        check(chk.dataset.state === 'in' && chk.getAttribute('aria-label') === 'Carol séries : dans la liste', 'filtre : 1 clic -> dans la liste');
        await until(() => queries.some(q => q.Ids), 'filtre : rechargement avec Ids');
        const filtered = queries.filter(q => q.Ids).pop();
        check(filtered?.Ids === sev.Id.toLowerCase() && filtered.ParentId === LIB && libQuery.Ids === undefined, 'filtre : Ids = titres de la liste (requête d\'origine intacte)');
        await hw.ApiClient.getItems(U.carol.id, { IncludeItemTypes: 'Movie', ParentId: 'other', Recursive: true });
        check(!lastQ().Ids, 'filtre : autre bibliothèque non filtrée');
        const noneChk = btn('Dans aucune watchlist');
        check(noneChk === $('.wl-chk-watchlist') && noneChk.dataset.list === 'none', 'filtre : « Dans aucune watchlist » en premier');
        // ✓ Zz A, ✗ Carol séries -> Dune seulement.
        btn('Zz A').click();
        chk.click();
        check(chk.dataset.state === 'out' && chk.getAttribute('aria-label') === 'Carol séries : pas dans la liste', 'filtre : 2 clics -> pas dans la liste');
        let q = await reloadLast();
        check(q.Ids === dune.Id.toLowerCase() && !q.ExcludeItemIds, 'filtre : dans « Zz A » mais pas dans « Carol séries » -> Dune seulement');
        // ✗ seul -> ExcludeItemIds.
        btn('Zz A').click(); btn('Zz A').click();
        q = await reloadLast();
        check(btn('Zz A').dataset.state === 'off' && !q.Ids && q.ExcludeItemIds === sev.Id.toLowerCase(), 'filtre : ✗ seul -> ExcludeItemIds');
        // ✓ Zz A + ✓ Carol séries -> union.
        chk.click(); chk.click(); btn('Zz A').click();
        q = await reloadLast();
        check(chk.dataset.state === 'in' && q.Ids?.split(',').sort().join() === [dune.Id, sev.Id].map(x => x.toLowerCase()).sort().join(), 'filtre : deux ✓ -> union des titres');
        // « Aucune » : exclusive, et exclut les titres de toutes les listes.
        noneChk.click();
        check(noneChk.dataset.state === 'in' && chk.dataset.state === 'off' && btn('Zz A').dataset.state === 'off', 'filtre : « aucune » remet les listes à zéro');
        q = await reloadLast();
        check(!q.Ids && q.ExcludeItemIds?.split(',').includes(sev.Id.toLowerCase()) && q.ExcludeItemIds.split(',').includes(dune.Id.toLowerCase()), 'filtre : « aucune » -> ExcludeItemIds = titres de mes listes');
        chk.click();
        check(noneChk.dataset.state === 'off', 'filtre : cocher une liste désactive « aucune »');
        chk.click(); chk.click();
        q = await reloadLast();
        check(chk.dataset.state === 'off' && !q.Ids && !q.ExcludeItemIds, 'filtre : 3 clics -> plus de filtre');
        await fetch(info.baseUrl + '/Watchlists/' + zzA.id, { method: 'DELETE', headers: hh });
        chk.click();
        await until(() => lastQ().Ids, 'filtre : recoché');
        dlg.querySelector('.resetFilters').click();
        q = await reloadLast();
        check(!q.Ids && chk.dataset.state === 'off', 'filtre : « Réinitialiser » retire le filtre');
        check(errs.length === 0, 'filtre : aucune erreur JS : ' + errs.join(' | '));
    }

    // ---------------- filtre « Watchlist » : vue React de Jellyfin 12 (menu MUI, requêtes axios / XHR)
    {
        const LIB = '0123456789abcdef0123456789abcdef';
        const sev = info.items.find(i => i.Name === 'Severance');
        const hh = { 'X-Test-User': U.carol.id };
        const hd = new JSDOM('<!doctype html><head></head><body></body>', { runScripts: 'dangerously', url: 'http://localhost/web/#/tv?topParentId=' + LIB + '&collectionType=tvshows' });
        const hw = hd.window; const errs = []; const sent = [];
        hw.addEventListener('error', e => errs.push(e.message));
        hw.requestAnimationFrame = cb => setTimeout(cb, 0);
        // XHR factice (installé avant header.js) : enregistre l'URL réellement ouverte et les en-têtes.
        const P = hw.XMLHttpRequest.prototype;
        P.open = function (m, u) { this.tUrl = u; this.tHeaders = []; };
        P.setRequestHeader = function (k, v) { if (!this.tHeaders) throw new Error('setRequestHeader avant open'); this.tHeaders.push(k + '=' + v); };
        P.send = function () { sent.push({ url: this.tUrl, headers: this.tHeaders }); };
        hw.ApiClient = {
            getCurrentUserId: () => U.carol.id,
            getUrl: p => info.baseUrl + '/' + p,
            getJSON: url => fetch(url, { headers: hh }).then(r => r.json()),
            async ajax(o) {
                const res = await fetch(o.url, { method: o.type, headers: hh, body: o.data });
                if (res.status >= 400) throw res;
                return res.json();
            }
        };
        const sc = hw.document.createElement('script'); sc.textContent = headerJs; hw.document.body.appendChild(sc);
        let focusEvents = 0;
        hw.addEventListener('visibilitychange', () => focusEvents++);
        const xhrGet = url => { const x = new hw.XMLHttpRequest(); x.open('GET', url, true); x.setRequestHeader('Authorization', 'tok'); x.send(); };
        const itemsUrl = 'http://srv/Items?userId=u&recursive=true&parentId=' + LIB + '&includeItemTypes=Series&startIndex=0';
        await sleep(50);
        const paper = hw.document.createElement('div');
        paper.className = 'MuiPaper-root';
        // Menu tel que le rend Jellyfin 12 à la première ouverture : tous les accordéons fermés, contenus démontés.
        const accHtml = (n, label) => '<div class="MuiAccordion-root" data-n="' + n + '"><div class="MuiAccordionSummary-root" role="button" aria-expanded="false" id="' + n + '-h">'
            + '<div class="MuiAccordionSummary-content"><p>' + label + '</p></div><div class="MuiAccordionSummary-expandIconWrapper"><svg></svg></div></div></div>';
        paper.innerHTML = accHtml('filters', 'Filtres') + accHtml('features', 'Caractéristiques') + accHtml('video', 'Type de vidéo')
            + '<button class="MuiButton-root Mui-disabled" disabled tabindex="-1">Réinitialiser les filtres</button>';
        // Bouton Filtres de la barre d'outils : point MUI masqué tant que hasFilters (filtres natifs) est faux.
        const fbtn = hw.document.createElement('button');
        fbtn.innerHTML = '<span class="MuiBadge-root"><svg></svg><span class="MuiBadge-badge MuiBadge-dot MuiBadge-invisible"></span></span>';
        hw.document.body.appendChild(fbtn);
        const fdot = fbtn.querySelector('.MuiBadge-badge');
        const resetBtn = paper.querySelector('button');
        const menuProps = { hasFilters: false };
        resetBtn['__reactFiber$test'] = { memoizedProps: { children: 'x' }, return: { memoizedProps: menuProps, return: null } };
        // Fibre React factice : le QueryClientProvider est un ancêtre du menu.
        const invalidated = [];
        paper['__reactFiber$test'] = { memoizedProps: {}, return: { memoizedProps: { children: 1 }, return: { memoizedProps: { client: { invalidateQueries: o => invalidated.push(o) } }, return: null } } };
        hw.document.body.appendChild(paper);
        const acc = await until(() => hw.document.querySelector('.wl-mui-acc'), 'MUI filtre : accordéon « Watchlist » (Filtres replié)');
        check(acc?.previousElementSibling?.dataset.n === 'filters' && acc.querySelector('.MuiAccordionSummary-content p')?.textContent === 'Watchlist', 'MUI filtre : accordéon « Watchlist » sous « Filtres »');
        check(acc?.dataset.n === 'features' && !acc.querySelector('[id="features-h"]') && acc.querySelector('[id="wl-features-h"]'), 'MUI filtre : copie de « Caractéristiques » avec ids uniques');
        const sum = acc?.querySelector('.MuiAccordionSummary-root');
        check(sum?.getAttribute('aria-expanded') === 'false' && acc.querySelector('[role="region"]').hidden, 'MUI filtre : fermé par défaut (aucun filtre actif)');
        sum?.click();
        check(sum?.getAttribute('aria-expanded') === 'true' && acc.classList.contains('Mui-expanded') && !acc.querySelector('[role="region"]').hidden, 'MUI filtre : ouverture au clic');
        await sleep(100);
        check(hw.document.querySelectorAll('.wl-mui-acc').length === 1, 'MUI filtre : un seul accordéon');
        const chk = await until(() => hw.document.querySelector('.wl-mui-acc .wl-chk-watchlist[data-name="Carol séries"]'), 'MUI filtre : case « Carol séries »');
        xhrGet(itemsUrl);
        check(sent.length === 1 && !sent[0].url.includes('ids='), 'MUI filtre : sans filtre, requête inchangée et envoyée tout de suite');
        chk.click();
        const pred = invalidated[0]?.predicate;
        check(invalidated.length === 1 && focusEvents === 0 && pred?.({ queryKey: ['User', 'u', 'Items', LIB, 'ViewByType', 'movies', {}] }) === true
            && pred({ queryKey: ['User', 'u', 'Views', {}] }) === false, 'MUI filtre : requêtes de la bibliothèque invalidées (clé ["User", id, "Items", ...])');
        xhrGet(itemsUrl);
        await until(() => sent.length === 2, 'MUI filtre : requête filtrée envoyée');
        const u = new URL(sent[1]?.url || 'http://x');
        check(u.searchParams.get('ids') === sev.Id.toLowerCase() && u.searchParams.get('parentId') === LIB, 'MUI filtre : ids = titres de la liste');
        check(sent[1]?.headers.join() === 'Authorization=tok', 'MUI filtre : en-têtes rejoués après le open différé');
        xhrGet('http://srv/Items?userId=u&recursive=true&parentId=' + LIB + '&includeItemTypes=BoxSet');
        xhrGet('http://srv/Items?userId=u&recursive=true&parentId=ffffffffffffffffffffffffffffffff&includeItemTypes=Series');
        check(sent.length === 4 && !sent[2].url.includes('ids=') && !sent[3].url.includes('ids='), 'MUI filtre : collections / autre bibliothèque non filtrées');
        chk.click();
        xhrGet(itemsUrl);
        await until(() => sent.length === 5, 'MUI filtre : requête ✗ envoyée');
        const xu = new URL(sent[4]?.url || 'http://x');
        check(!xu.searchParams.get('ids') && xu.searchParams.get('excludeItemIds') === sev.Id.toLowerCase(), 'MUI filtre : ✗ -> excludeItemIds');
        chk.click();
        xhrGet(itemsUrl);
        check(sent.length === 6 && !sent[5].url.includes('ids=') && !sent[5].url.includes('excludeItemIds=') && invalidated.length === 3, 'MUI filtre : 3 clics -> plus de filtre');
        const noneM = hw.document.querySelector('.wl-mui-acc .wl-chk-watchlist[data-list="none"]');
        noneM.click();
        xhrGet(itemsUrl);
        await until(() => sent.length === 7, 'MUI filtre : requête « aucune » envoyée');
        const nu = new URL(sent[6]?.url || 'http://x');
        check(!nu.searchParams.get('ids') && nu.searchParams.get('excludeItemIds')?.split(',').includes(sev.Id.toLowerCase()), 'MUI filtre : « aucune » -> excludeItemIds');
        noneM.click();
        // Réinitialiser les filtres / point du bouton Filtres
        check(resetBtn.disabled && !fdot.classList.contains('wl-badge-on'), 'MUI reset : sans filtre, bouton désactivé et point éteint');
        chk.click();
        check(!resetBtn.disabled && !resetBtn.classList.contains('Mui-disabled') && fdot.classList.contains('wl-badge-on'), 'MUI reset : notre filtre seul -> bouton réactivé, point allumé');
        const before = invalidated.length;
        resetBtn.click();
        check(chk.getAttribute('data-state') === 'off' && invalidated.length === before + 1, 'MUI reset : filtre effacé et bibliothèque rechargée');
        check(resetBtn.disabled && resetBtn.classList.contains('Mui-disabled') && !fdot.classList.contains('wl-badge-on'), 'MUI reset : bouton redésactivé, point éteint');
        xhrGet(itemsUrl);
        check(!sent[sent.length - 1].url.includes('ids='), 'MUI reset : requête suivante non filtrée');
        // Filtre natif actif (hasFilters vrai) : on ne désactive jamais ce que Jellyfin a activé.
        menuProps.hasFilters = true;
        resetBtn.disabled = false; resetBtn.classList.remove('Mui-disabled');
        chk.click(); chk.click(); chk.click();
        check(!resetBtn.disabled, 'MUI reset : filtre natif actif -> bouton laissé actif');
        check(errs.length === 0, 'MUI filtre : aucune erreur JS : ' + errs.join(' | '));
    }

    // ---------------- titres absents : ajout depuis TMDB, statut, demande, filtres
    {
        const z = await openPage(U.alice);
        z.click(await until(() => z.$('[data-act="new"]'), 'accueil (absents)'));
        const fz = await until(() => z.$('form[data-form="create"]'), 'form absents');
        fz.elements.name.value = 'Films à télécharger';
        fz.dispatchEvent(new z.w.Event('submit', { bubbles: true, cancelable: true }));
        const sz = await until(() => z.$('[data-search]'), 'liste absents ouverte');
        sz.value = 'dune'; sz.dispatchEvent(new z.w.Event('input', { bubbles: true }));
        z.click(await until(() => z.$('[data-add^="i"]'), 'Dune (serveur)'));
        await until(() => z.$$('.wl-item').length === 1, 'Dune ajouté (absents)');
        const sz2 = z.$('[data-search]');
        sz2.value = 'deux'; sz2.dispatchEvent(new z.w.Event('input', { bubbles: true }));
        z.click(await until(() => z.$('[data-add^="t"]'), 'Dune 2 (TMDB)'));
        const absentEl = await until(() => z.$('.wl-item.absent'), 'titre absent affiché');
        check(absentEl?.querySelector('img')?.getAttribute('src') === 'https://image.tmdb.org/t/p/w342/dune2.jpg', 'affiche TMDB du titre absent');
        check(z.$('h2')?.textContent.includes('1 sur le serveur'), 'compteur « sur le serveur »');
        const reqBtn = await until(() => z.$('.wl-item.absent [data-act="request"]'), 'bouton Demander');
        check(z.$('.wl-item.absent .wl-status')?.textContent.includes('Pas sur le serveur'), 'badge « Pas sur le serveur »');
        check(!z.$('.wl-item:not(.absent) [data-act="request"]'), 'pas de bouton Demander sur un titre présent');
        z.click(reqBtn);
        await until(() => z.$('.wl-item.absent .wl-status')?.textContent.includes('Demandé'), 'badge « Demandé » après la demande');
        check(!z.$('.wl-item.absent [data-act="request"]'), 'bouton Demander retiré');
        z.click(z.$('[data-filter="off"]'));
        await until(() => z.$$('.wl-item').length === 1 && z.$('.wl-item.absent'), 'filtre « Pas sur le serveur »');
        z.click(z.$('[data-filter="on"]'));
        await until(() => z.$$('.wl-item').length === 1 && !z.$('.wl-item.absent'), 'filtre « Sur le serveur »');
        z.click(z.$('[data-filter="all"]'));
        await until(() => z.$$('.wl-item').length === 2, 'filtre « Tous »');
        // statut conservé à la réouverture (vient de Jellyseerr)
        z.click(z.$('[data-act="back"]'));
        const card = await until(() => [...z.$$('.wl-card')].find(c => c.textContent.includes('Films à télécharger')), 'carte absents');
        check(card?.textContent.includes('2 titres') && card?.textContent.includes('1 sur le serveur'), 'carte : « 1 sur le serveur »');
        z.click(card);
        await until(() => z.$('.wl-item.absent .wl-status')?.textContent.includes('Demandé'), 'statut Demandé rechargé depuis Jellyseerr');
        check(z.errors.length === 0, 'aucune erreur JS (absents) : ' + z.errors.join(' | '));
    }

    // ---------------- titres vus (griser) + tirage au sort (page)
    {
        const hb = { 'X-Test-User': U.bob.id }, hc = { 'X-Test-User': U.carol.id };
        const req = (h, method, url, body) => fetch(info.baseUrl + url, { method, headers: { ...h, 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) }).then(r => r.json());
        const dune = info.items.find(i => i.Name === 'Dune'), sev = info.items.find(i => i.Name === 'Severance');
        const duo = await req(hb, 'POST', '/Watchlists', { name: 'Soirées bob & carol' });
        await req(hb, 'POST', '/Watchlists/' + duo.id + '/Items', { itemId: dune.Id });
        await req(hb, 'POST', '/Watchlists/' + duo.id + '/Items', { itemId: sev.Id });
        await req(hb, 'POST', '/Watchlists/' + duo.id + '/Members', { userId: U.carol.id, role: 'Editor' });
        await req(hc, 'POST', '/Watchlists/' + duo.id + '/Invitation/Accept');
        for (const u of [U.bob, U.carol]) await fetch(info.baseUrl + '/Test/Played?user=' + u.id + '&item=' + dune.Id, { method: 'POST' });

        const z = await openPage(U.bob);
        z.click(await until(() => z.$('[data-open="' + duo.id + '"]'), 'vus : carte de la liste commune'));
        z.click(await until(() => z.$('[data-act="settings"]'), 'vus : bouton Paramètres'));
        const fu = await until(() => z.$('form[data-form="update"]'), 'vus : formulaire de paramètres');
        check(fu?.elements.seenMode.value === 'Keep' && fu.textContent.includes('Titres vus (par tous les membres)'), 'vus : réglage « Les garder » par défaut');
        check(!z.$('.wl-item.seen'), 'vus : mode Keep -> rien de grisé');
        fu.elements.seenMode.value = 'Dim';
        fu.dispatchEvent(new z.w.Event('submit', { bubbles: true, cancelable: true }));
        const seenEl = await until(() => z.$('.wl-item.seen'), 'vus : titre vu par tous grisé');
        const itemsEls = z.$$('.wl-item');
        check(seenEl === itemsEls[itemsEls.length - 1] && seenEl.textContent.includes('Dune') && seenEl.textContent.includes('Vu par tous'), 'vus : grisé, mis en bas, « Vu par tous »');
        check(!seenEl.querySelector('[data-act="up"]') && !seenEl.querySelector('[data-act="down"]') && !!seenEl.querySelector('[data-act="remove"]'), 'vus : pas de flèches sur un titre grisé');
        check(z.$$('.wl-item.seen').length === 1, 'vus : la série non vue reste normale');

        z.click(z.$('[data-act="back"]'));
        z.click(await until(() => z.$('[data-act="pick"]'), 'tirage : bouton « Que regarder ? »'));
        const carolBox = () => z.$$('.wl-pick-panel [data-pick-member]').find(x => x.value.replace(/-/g, '') === U.carol.id);
        const carolChk = await until(carolBox, 'tirage : carol cochable');
        check(z.$('[data-act="pick"]').getAttribute('aria-expanded') === 'true', 'tirage : panneau ouvert');
        carolChk.checked = true;
        carolChk.dispatchEvent(new z.w.Event('change', { bubbles: true }));
        const api = await req(hb, 'GET', '/Watchlists/Pick?members=' + U.carol.id + '&includeSeen=false');
        const n = api.candidates.length;
        await until(() => carolBox()?.checked && z.$('.wl-pick-count')?.textContent === n + ' titre' + (n > 1 ? 's' : '') + ' possible' + (n > 1 ? 's' : ''), 'tirage avec carol : nombre de titres');
        check(z.$('.wl-pick-panel').textContent.includes('« Soirées bob & carol »'), 'tirage avec carol : liste commune utilisée');
        check(!api.candidates.some(c => c.title === 'Dune') && api.candidates.some(c => c.title === 'Severance'), 'tirage avec carol : Dune (vu) écarté');
        const type = z.$('[data-pick="type"]');
        type.value = 'Series'; type.dispatchEvent(new z.w.Event('change', { bubbles: true }));
        z.click(z.$('[data-act="pick-draw"]'));
        const result = await until(() => z.$('.wl-pick-result h3'), 'tirage : résultat affiché');
        check(api.candidates.some(c => c.type === 'Series' && result?.textContent.startsWith(c.title)) && z.$('.wl-pick-result').textContent.includes('Série'), 'tirage : une série des candidats');
        check(z.$('.wl-pick-result a.wl-btn')?.getAttribute('href').startsWith('#/details?id='), 'tirage : lien vers la fiche');
        type.value = 'Movie'; type.dispatchEvent(new z.w.Event('change', { bubbles: true }));
        const movies = api.candidates.filter(c => c.type === 'Movie').length;
        check(z.$('.wl-pick-count').textContent === (movies ? movies + ' titre' + (movies > 1 ? 's' : '') + ' possible' + (movies > 1 ? 's' : '') : 'Aucun titre possible'), 'tirage : filtre Films -> compteur mis à jour');
        const seen = z.$('[data-pick-seen]');
        seen.checked = true; seen.dispatchEvent(new z.w.Event('change', { bubbles: true }));
        await until(() => z.$('[data-pick-seen]')?.checked && z.$('[data-pick="type"]')?.value === 'Movie' && z.$('.wl-pick-count')?.textContent !== 'Aucun titre possible', 'tirage : « inclure les déjà vus » -> Dune possible');
        z.click(z.$('[data-act="pick"]'));
        check(!z.$('.wl-pick-panel'), 'tirage : panneau refermé');
        check(z.errors.length === 0, 'vus / tirage : aucune erreur JS : ' + z.errors.join(' | '));
    }

    // ---------------- page de configuration admin
    {
        const fs = await import('node:fs');
        const cfgHtml = fs.readFileSync(path.join(here, '../../src/Jellyfin.Plugin.Watchlists/Web/config.html'), 'utf8');
        const cd = new JSDOM(cfgHtml, { runScripts: 'dangerously', url: 'http://localhost/web/#/configurationpage?name=Watchlists' });
        const cw = cd.window; const errs = []; cw.addEventListener('error', e => errs.push(e.message));
        let stored = { SeerrUrl: 'http://seerr:5055', SeerrApiKey: 'k', TagEnabled: true, TagName: 'watchlist', LastAppliedTagName: 'watchlist' };
        const posts = [];
        cw.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
        cw.ApiClient = {
            getPluginConfiguration: async () => JSON.parse(JSON.stringify(stored)),
            updatePluginConfiguration: async (id, c) => { stored = c; return {}; },
            getUrl: p => 'http://x/' + p,
            ajax: async o => { posts.push(o.url); return o.url.endsWith('TestSeerr') ? { ok: true, version: '2.7.3' } : { onServer: 3, inWatchlists: 5, tagged: 1, untagged: 0 }; }
        };
        // jsdom n'exécute pas les scripts inline déjà présents avant que les globals existent : on réinjecte.
        cw.document.querySelectorAll('script').forEach(old => { const sc = cw.document.createElement('script'); sc.textContent = old.textContent; old.replaceWith(sc); });
        const pg = cw.document.querySelector('#WatchlistsConfigPage');
        pg.dispatchEvent(new cw.Event('pageshow'));
        await until(() => cw.document.querySelector('#wlSeerrUrl').value === 'http://seerr:5055', 'config : valeurs chargées');
        check(cw.document.querySelector('#wlTagName').value === 'watchlist' && cw.document.querySelector('#wlTagEnabled').checked, 'config : tag chargé');
        cw.document.querySelector('#wlTagName').value = 'a-voir';
        cw.document.querySelector('#WatchlistsConfigForm').dispatchEvent(new cw.Event('submit', { cancelable: true }));
        await until(() => stored.TagName === 'a-voir' && stored.LastAppliedTagName === 'watchlist', 'config : enregistrement (LastAppliedTagName préservé)');
        await until(() => posts.some(u => u.endsWith('Watchlists/Admin/SyncTags')), 'config : synchro lancée après enregistrement');
        cw.document.querySelector('#wlTestSeerr').click();
        await until(() => cw.document.querySelector('#wlTestResult').textContent.includes('2.7.3'), 'config : test de connexion');
        cw.document.querySelector('#wlSyncTags').click();
        await until(() => cw.document.querySelector('#wlSyncResult').textContent.includes('3 / 5'), 'config : résultat de synchro');
        check(errs.length === 0, 'config : aucune erreur JS : ' + errs.join(' | '));
    }
} catch (e) {
    failures++; console.log('EXCEPTION', e);
} finally {
    proc.kill();
}
console.log(`${pass} OK, ${failures} échec(s)`);
process.exit(failures ? 1 : 0);
