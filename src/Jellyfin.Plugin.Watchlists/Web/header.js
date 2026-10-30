// Watchlists : entrée dans l'en-tête du client web, injectée dans index.html par File Transformation.
// - Barre de navigation Jellyfin 12 (Favoris, Films, Séries...) visible : lien texte « Watchlist » juste après Favoris.
// - Sinon (ancien en-tête, petit écran sans barre de navigation) : icône à côté des boutons de droite.
// Ouvre la page Watchlists de Plugin Pages ; pastille quand des invitations sont en attente.
(function () {
    'use strict';
    if (window.__watchlistsHeader) return;
    window.__watchlistsHeader = true;

    var PAGE = '#/userpluginsettings.html?pageUrl=/Watchlists/Web/watchlists.html';
    var BTN_CLASS = 'watchlistsHeaderButton';
    var NAV_CLASS = 'watchlistsNavLink';
    var NAV_LABEL = 'Watchlist';
    var lastInviteCheck = 0;
    var inviteCount = 0;

    var style = document.createElement('style');
    style.textContent =
        '.' + BTN_CLASS + '{position:relative}' +
        '.' + BTN_CLASS + ' .wl-dot{position:absolute;top:.35em;right:.35em;min-width:1.1em;height:1.1em;padding:0 .25em;border-radius:999px;' +
        'background:#d9534f;color:#fff;font-size:.6em;line-height:1.1em;text-align:center;font-weight:bold;pointer-events:none}' +
        // Barre MUI de Jellyfin 12 : aligne la taille sur les IconButton natifs (48px / icône 24px).
        '.MuiToolbar-root .' + BTN_CLASS + '{display:inline-flex!important;align-items:center;justify-content:center;width:48px;height:48px;padding:0!important;margin:0!important;font-size:16px!important}' +
        '.MuiToolbar-root .' + BTN_CLASS + ' > .material-icons{font-size:24px!important}' +
        '.MuiToolbar-root .' + BTN_CLASS + ' .wl-dot{font-size:10px}' +
        '.' + NAV_CLASS + ' .wl-nav-count{display:inline-block;min-width:1.4em;margin-left:.4em;padding:0 .35em;border-radius:999px;background:#d9534f;color:#fff;font-size:.75em;line-height:1.4em;text-align:center}';
    document.head.appendChild(style);

    function client() {
        var c = window.ApiClient;
        return c && typeof c.getCurrentUserId === 'function' && c.getCurrentUserId() ? c : null;
    }

    function renderNav(link) {
        var count = link.querySelector('.wl-nav-count');
        if (inviteCount > 0) {
            if (!count) {
                count = document.createElement('span');
                count.className = 'wl-nav-count';
                link.appendChild(count);
            }
            count.textContent = inviteCount > 9 ? '9+' : String(inviteCount);
            link.title = inviteCount + ' invitation' + (inviteCount > 1 ? 's' : '') + ' en attente';
        } else {
            if (count) count.remove();
            link.removeAttribute('title');
        }

        // État actif : même couleur que Favoris quand il est sélectionné (couleur « primary » du thème).
        var active = window.location.hash.indexOf('pageUrl=/Watchlists/Web/watchlists.html') >= 0;
        if (active) {
            var primary = document.querySelector('.MuiToolbar-root .MuiButton-colorPrimary:not(.' + NAV_CLASS + ')');
            link.style.color = primary ? window.getComputedStyle(primary).color : '#00a4dc';
        } else {
            link.style.color = '';
        }
    }

    function render(btn) {
        var dot = btn.querySelector('.wl-dot');
        if (inviteCount > 0) {
            if (!dot) {
                dot = document.createElement('span');
                dot.className = 'wl-dot';
                btn.appendChild(dot);
            }
            dot.textContent = inviteCount > 9 ? '9+' : String(inviteCount);
            btn.title = 'Watchlists : ' + inviteCount + ' invitation' + (inviteCount > 1 ? 's' : '') + ' en attente';
        } else {
            if (dot) dot.remove();
            btn.title = 'Watchlists';
        }
    }

    function refreshInvites(force) {
        var c = client();
        if (!c) return;
        var now = Date.now();
        if (!force && now - lastInviteCheck < 60000) return;
        lastInviteCheck = now;
        c.getJSON(c.getUrl('Watchlists/Invitations')).then(function (list) {
            inviteCount = Array.isArray(list) ? list.length : 0;
            document.querySelectorAll('.' + BTN_CLASS).forEach(render);
            document.querySelectorAll('.' + NAV_CLASS).forEach(renderNav);
        }).catch(function () { /* plugin indisponible ou non connecté : on ignore */ });
    }

    function createButton() {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'headerButton headerButtonRight paper-icon-button-light ' + BTN_CLASS;
        btn.title = 'Watchlists';
        btn.setAttribute('aria-label', 'Watchlists');
        var icon = document.createElement('span');
        // Ligature (comme Jellyfin Enhanced) : fonctionne avec la police Material Icons de Jellyfin 10.x et 12.x.
        icon.className = 'material-icons';
        icon.textContent = 'playlist_add_check';
        icon.setAttribute('aria-hidden', 'true');
        btn.appendChild(icon);
        btn.addEventListener('click', function () {
            window.location.hash = PAGE;
            refreshInvites(true);
        });
        render(btn);
        return btn;
    }

    function isShown(el) {
        for (var e = el; e && e.nodeType === 1; e = e.parentElement) {
            if (window.getComputedStyle(e).display === 'none') return false;
        }
        return true;
    }

    // Conteneur des boutons d'en-tête. Jellyfin 12 (layout MUI, par défaut) garde l'ancien .headerRight
    // dans le DOM mais masqué (display:none) : on utilise alors la zone de boutons de la Toolbar MUI,
    // juste avant le menu utilisateur (même logique que Jellyfin Enhanced).
    function getContainer() {
        var legacy = Array.prototype.filter.call(document.querySelectorAll('.headerRight:not(.wl-own-tray)'), isShown)[0];
        if (legacy) {
            return { el: legacy, legacy: true };
        }
        var userBtn = document.querySelector('[aria-controls="app-user-menu"]');
        var toolbar = (userBtn && userBtn.closest('.MuiToolbar-root')) || document.querySelector('.MuiAppBar-root .MuiToolbar-root');
        if (!toolbar) return legacy ? { el: legacy, legacy: true } : null;
        var userBox = userBtn;
        while (userBox && userBox.parentElement !== toolbar) userBox = userBox.parentElement;
        var tray = userBox ? userBox.previousElementSibling : null;
        if (tray && !tray.matches('button, a, [role="button"]') && isShown(tray)) {
            return { el: tray, legacy: false };
        }
        var own = toolbar.querySelector(':scope > .wl-own-tray');
        if (!own) {
            own = document.createElement('div');
            own.className = 'headerRight wl-own-tray';
            own.style.display = 'flex';
            if (userBox) toolbar.insertBefore(own, userBox); else toolbar.appendChild(own);
        }
        return { el: own, legacy: false };
    }

    // Lien « Favoris » de la barre de navigation Jellyfin 12 (bouton MUI <a href="#/home?tab=1">).
    function findFavorites() {
        var links = document.querySelectorAll('.MuiToolbar-root a[href="#/home?tab=1"], .MuiToolbar-root a[href$="#/home?tab=1"]');
        for (var i = 0; i < links.length; i++) {
            if (!links[i].classList.contains(NAV_CLASS) && isShown(links[i])) return links[i];
        }
        return null;
    }

    // Clone d'un bouton de la barre (même style MUI que Favoris / Films / Séries), sans icône d'origine ni ripple.
    function createNavLink(fav) {
        // Gabarit non sélectionné : si Favoris est actif (couleur primary), on prend les classes d'un voisin inactif.
        var template = fav;
        if (fav.classList.contains('MuiButton-colorPrimary')) {
            var sib = fav.parentElement && fav.parentElement.querySelector('a.MuiButton-colorInherit');
            if (sib) template = sib;
        }

        var link = document.createElement('a');
        link.className = template.className + ' ' + NAV_CLASS;
        link.href = PAGE;
        link.setAttribute('data-watchlists', 'nav');
        var startIcon = fav.querySelector('.MuiButton-startIcon');
        if (startIcon) {
            var iconWrap = document.createElement('span');
            iconWrap.className = startIcon.className;
            var icon = document.createElement('span');
            icon.className = 'material-icons notranslate';
            icon.setAttribute('aria-hidden', 'true');
            icon.style.fontSize = '20px';
            icon.textContent = 'playlist_add_check';
            iconWrap.appendChild(icon);
            link.appendChild(iconWrap);
        }

        link.appendChild(document.createTextNode(NAV_LABEL));
        link.addEventListener('click', function () { refreshInvites(true); });
        renderNav(link);
        return link;
    }

    // Retourne true si le lien texte est en place (dans ce cas pas d'icône).
    function ensureNavLink() {
        var fav = findFavorites();
        var links = document.querySelectorAll('.' + NAV_CLASS);
        if (!fav) {
            links.forEach(function (l) { if (!isShown(l)) l.remove(); });
            return false;
        }

        var inPlace = false;
        links.forEach(function (l) {
            if (l.previousElementSibling === fav) inPlace = true;
            else l.remove();
        });
        if (!inPlace) {
            fav.parentNode.insertBefore(createNavLink(fav), fav.nextSibling);
            refreshInvites(true);
        }

        return true;
    }

    function ensureButton() {
        if (!client()) return;
        if (ensureNavLink()) {
            document.querySelectorAll('.' + BTN_CLASS).forEach(function (b) { b.remove(); });
            return;
        }

        var target = getContainer();
        if (!target) return;
        var existing = document.querySelectorAll('.' + BTN_CLASS);
        var inPlace = false;
        existing.forEach(function (b) {
            if (b.parentElement === target.el) inPlace = true;
            else b.remove(); // bouton orphelin (ex. resté dans l'en-tête legacy masqué)
        });
        if (inPlace) return;
        var btn = createButton();
        if (target.legacy) {
            // Avant le bouton cast (à côté des icônes Jellyfin Enhanced), sinon avant recherche / utilisateur.
            var anchor = target.el.querySelector('.headerCastButton') ||
                target.el.querySelector('.headerSearchButton') ||
                target.el.querySelector('.headerUserButton');
            target.el.insertBefore(btn, anchor && anchor.parentElement === target.el ? anchor : null);
        } else {
            target.el.insertBefore(btn, target.el.firstChild);
        }
        refreshInvites(true);
    }

    // ------------------------------------------------------------------ bouton sur les fiches (films / séries)
    // Même bouton au survol des affiches (cartes) des films et séries, dans la zone Vu / Favori / « ... ».
    // Une seule watchlist modifiable : le clic ajoute / retire directement.
    // Plusieurs (ou aucune) : mini menu en infobulle sous le bouton, une case par liste + « Nouvelle watchlist… ».

    var DETAIL_CLASS = 'watchlistsDetailButton';
    var CARD_CLASS = 'watchlistsCardButton';
    var MENU_CLASS = 'wl-detail-menu';
    var openMenu = null;

    style.textContent +=
        '.' + DETAIL_CLASS + '.wl-in .detailButton-icon{color:#00a4dc}' +
        '.' + DETAIL_CLASS + '[disabled]{opacity:.5}' +
        '.' + CARD_CLASS + '.wl-in .wl-icon{color:#00a4dc}' +
        '.' + CARD_CLASS + '[disabled]{opacity:.5}' +
        '.' + MENU_CLASS + '{position:fixed;z-index:10000;min-width:13em;max-width:20em;padding:.35em 0;border-radius:.4em;background:#202020;color:#fff;' +
        'box-shadow:0 .3em 1.2em rgba(0,0,0,.6);font-size:.95em}' +
        '.' + MENU_CLASS + '::before{content:"";position:absolute;top:-.45em;left:var(--wl-arrow,50%);margin-left:-.45em;border:.45em solid transparent;border-top:0;border-bottom-color:#202020}' +
        '.' + MENU_CLASS + '.wl-above::before{top:auto;bottom:-.45em;border-bottom:0;border-top:.45em solid #202020}' +
        '.' + MENU_CLASS + ' .wl-title{padding:.35em 1em .45em;opacity:.6;font-size:.85em;text-transform:uppercase;letter-spacing:.05em}' +
        '.' + MENU_CLASS + ' button{display:flex;align-items:center;gap:.6em;width:100%;padding:.55em 1em;border:0;background:none;color:inherit;font:inherit;text-align:left;cursor:pointer}' +
        '.' + MENU_CLASS + ' button:hover,.' + MENU_CLASS + ' button:focus-visible{background:rgba(255,255,255,.1);outline:0}' +
        '.' + MENU_CLASS + ' button[disabled]{opacity:.5;cursor:default}' +
        '.' + MENU_CLASS + ' button .material-icons{font-size:1.3em}' +
        '.' + MENU_CLASS + ' .wl-on .material-icons{color:#00a4dc}' +
        '.' + MENU_CLASS + ' .wl-name{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
        '.' + MENU_CLASS + ' .wl-group{display:flex;align-items:center;gap:.45em;margin-top:.2em;padding:.5em 1em .3em;font-size:.78em;font-weight:600;text-transform:uppercase;letter-spacing:.06em;color:var(--wl-group)}' +
        '.' + MENU_CLASS + ' .wl-group .material-icons{font-size:1.35em}' +
        '.' + MENU_CLASS + ' .wl-group + button{margin-top:.1em}' +
        '.' + MENU_CLASS + ' .wl-in-group{box-shadow:inset 3px 0 0 var(--wl-group)}' +
        '.' + MENU_CLASS + ' .wl-tag{font-size:1.05em!important;opacity:.55}' +
        '.' + MENU_CLASS + ' .wl-sep{height:1px;margin:.3em 0;background:rgba(255,255,255,.12)}' +
        '.' + MENU_CLASS + ' .wl-empty{padding:.4em 1em;opacity:.7;font-size:.9em}' +
        '.wl-toast{position:fixed;left:50%;bottom:2em;z-index:10001;transform:translateX(-50%);padding:.7em 1.2em;border-radius:.3em;background:#303030;color:#fff;' +
        'box-shadow:0 .2em .8em rgba(0,0,0,.5);transition:opacity .3s}' +
        '.wl-match{position:fixed;right:1em;bottom:5em;z-index:10001;display:grid;gap:.6em;width:min(30em,calc(100vw - 16px));font-size:16px}' +
        // Ancrée sous le bouton cliqué, à la place du menu (même flèche).
        '.wl-match.wl-anchored{right:auto;bottom:auto}' +
        '.wl-match.wl-anchored::before{content:"";position:absolute;top:-.5em;left:var(--wl-arrow,50%);margin-left:-.5em;border:.5em solid transparent;border-top:0;border-bottom-color:#202020}' +
        '.wl-match.wl-anchored.wl-above::before{top:auto;bottom:-.5em;border-bottom:0;border-top:.5em solid #202020}' +
        '.wl-mui{padding:.3em 0 .4em}' +
        '.wl-fhelp{margin:.1em 0 .3em;font-size:.8em;opacity:.6}' +
        '.wl-fopt{display:flex;align-items:center;gap:.8em;width:100%;padding:.45em 0;border:0;background:none;color:inherit;font:inherit;font-size:1rem;line-height:1.5;text-align:left;cursor:pointer}' +
        '.wl-fopt:focus-visible{outline:0}' +
        '.wl-fopt:focus-visible .wl-box{outline:2px solid #00a4dc;outline-offset:2px}' +
        '.wl-fopt .wl-box{display:flex;align-items:center;justify-content:center;box-sizing:border-box;flex:none;width:18px;height:18px;margin:0 3px;' +
        'border:2px solid rgba(255,255,255,.7);border-radius:2px;color:#fff;font-size:12px;font-weight:bold;line-height:1}' +
        '.wl-fopt[data-state="in"] .wl-box{background:#00a4dc;border-color:#00a4dc}' +
        '.wl-fopt[data-state="in"] .wl-box::after{content:"";width:5px;height:10px;margin-top:-2px;border:solid #fff;border-width:0 2px 2px 0;transform:rotate(45deg)}' +
        '.wl-fopt[data-state="out"] .wl-box{background:#d9534f;border-color:#d9534f}' +
        '.wl-fopt[data-state="out"] .wl-box::after{content:"\\2715"}' +
        '.wl-fopt[data-state="out"] .wl-fname{text-decoration:line-through;opacity:.8}' +
        '.wl-fopt.wl-none{margin-bottom:.35em;padding-bottom:.6em;border-bottom:1px solid rgba(255,255,255,.12)}' +
        '.wl-match-row{padding:1.1em 1.3em;border-radius:.5em;background:#202020;color:#fff;box-shadow:0 .3em 1.2em rgba(0,0,0,.6);font-size:1.05em;line-height:1.4}' +
        '.wl-match-row p{margin:0 0 .9em}' +
        '.wl-match-row .wl-actions{display:flex;flex-wrap:wrap;gap:.5em}' +
        '.wl-match-row button{padding:.6em 1.1em;border:0;border-radius:.3em;background:rgba(255,255,255,.12);color:inherit;font:inherit;font-size:.95em;cursor:pointer;' +
        'max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
        '.wl-match-row button:hover,.wl-match-row button:focus-visible{background:rgba(255,255,255,.2);outline:0}' +
        '.wl-match-row button.wl-primary{background:#00a4dc}' +
        '.wl-match-row button.wl-primary:hover{background:#0cb0e8}' +
        '.wl-match-row button[disabled]{opacity:.5;cursor:default}' +
        '.wl-del-backdrop{position:fixed;inset:0;z-index:10002;display:flex;align-items:center;justify-content:center;padding:16px;background:rgba(0,0,0,.6);font-size:16px}' +
        '.wl-del-backdrop .wl-match-row{width:min(32em,100%);max-height:calc(100vh - 32px);overflow:auto}' +
        '.wl-del-backdrop h3{margin:0 0 .6em;font-size:1.2em}' +
        '.wl-del-backdrop ul{margin:0 0 1em;padding-left:1.2em}' +
        '.wl-del-backdrop li{margin:.2em 0}' +
        '.wl-del-backdrop .wl-del-owner{opacity:.7}' +
        '.wl-del-backdrop .wl-actions{justify-content:flex-end}' +
        // Point du bouton Filtres allumé pour notre filtre (MUI le masque par scale(0) tant que hasFilters est faux).
        '.MuiBadge-badge.MuiBadge-invisible.wl-badge-on{transform:scale(1) translate(50%,-50%)}' +
        '.wl-match-row button.wl-danger{background:#d9534f}' +
        '.wl-match-row button.wl-danger:hover{background:#e0605c}';

    function toast(text) {
        var el = document.createElement('div');
        el.className = 'wl-toast';
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(function () { el.style.opacity = '0'; }, 2500);
        setTimeout(function () { el.remove(); }, 2900);
    }

    // Même gestion d'erreur que la page : {"error": "..."} renvoyé par l'API.
    function api(method, path, body) {
        var c = client();
        var opts = { type: method, url: c.getUrl(path), dataType: 'json' };
        if (body) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        return c.ajax(opts).catch(function (err) {
            if (err && typeof err.json === 'function') {
                return err.json().then(function (j) { throw new Error(j && j.error ? j.error : ('Erreur ' + err.status)); },
                    function () { throw new Error('Erreur ' + err.status); });
            }
            throw err instanceof Error ? err : new Error('Erreur réseau');
        });
    }

    // ------------------------------------------------------------------ proposition de liste commune
    // Après un ajout, l'API renvoie les autres utilisateurs qui ont aussi le titre (matches) : on propose
    // de l'ajouter à une liste commune existante, ou d'en créer une (l'autre utilisateur est invité).
    // Exposé pour la page Watchlists (même fenêtre).

    var MATCH_TIMEOUT = 20000;

    function matchButton(label, primary) {
        var b = document.createElement('button');
        b.type = 'button';
        b.textContent = label;
        b.title = label;
        if (primary) b.className = 'wl-primary';
        return b;
    }

    // Proposition ancrée sous un bouton : fermée comme le menu (clic à côté, Échap, défilement).
    var anchoredPanel = null;

    function closeAnchored() {
        if (!anchoredPanel) return;
        anchoredPanel.remove();
        anchoredPanel = null;
        document.removeEventListener('mousedown', onAnchoredOutside, true);
        document.removeEventListener('keydown', onAnchoredKey, true);
        window.removeEventListener('scroll', closeAnchored, true);
        window.removeEventListener('resize', closeAnchored);
    }

    function onAnchoredOutside(e) {
        if (anchoredPanel && !anchoredPanel.contains(e.target)) closeAnchored();
    }

    function onAnchoredKey(e) {
        if (e.key === 'Escape' && anchoredPanel) {
            e.stopPropagation();
            closeAnchored();
        }
    }

    // anchor : bouton cliqué (fiche, affiche) ; sans ancre (page Watchlists), en bas à droite.
    function showMatches(result, sourceId, onChange, anchor) {
        var matches = result && Array.isArray(result.matches) ? result.matches : [];
        if (!matches.length || !result.addedEntryId) return;
        var anchored = !!(anchor && anchor.isConnected);
        var panel;
        if (anchored) {
            closeMenu();
            closeAnchored();
            panel = document.createElement('div');
            panel.className = 'wl-match wl-anchored';
            anchoredPanel = panel;
        } else {
            panel = document.querySelector('.wl-match:not(.wl-anchored)');
            if (!panel) {
                panel = document.createElement('div');
                panel.className = 'wl-match';
            }
        }
        panel.setAttribute('role', 'region');
        panel.setAttribute('aria-label', 'Watchlists communes');
        document.body.appendChild(panel);

        matches.forEach(function (m) {
            var row = document.createElement('div');
            row.className = 'wl-match-row';
            var text = document.createElement('p');
            var lists = m.lists || [];
            text.textContent = m.user.name + ' a aussi ce titre dans sa watchlist. ' +
                (lists.length ? 'L\u2019ajouter à votre liste commune ?' : 'Créer une watchlist commune ?');
            var actions = document.createElement('div');
            actions.className = 'wl-actions';
            row.appendChild(text);
            row.appendChild(actions);

            var timer = null;
            function close() {
                clearTimeout(timer);
                row.remove();
                if (panel.children.length) return;
                if (panel === anchoredPanel) closeAnchored();
                else panel.remove();
            }
            function arm() {
                clearTimeout(timer);
                if (!anchored) timer = setTimeout(close, MATCH_TIMEOUT);
            }
            function run(req, okText) {
                actions.querySelectorAll('button').forEach(function (b) { b.disabled = true; });
                req.then(function (w) {
                    toast(typeof okText === 'function' ? okText(w) : okText);
                    close();
                    if (onChange) onChange();
                }, function (e) {
                    toast(e.message);
                    actions.querySelectorAll('button').forEach(function (b) { b.disabled = false; });
                });
            }

            lists.slice(0, 3).forEach(function (l) {
                var b = matchButton('Ajouter à « ' + l.name + ' »', true);
                b.addEventListener('click', function () {
                    run(api('POST', 'Watchlists/' + l.id + '/Items', { fromWatchlistId: sourceId, fromEntryId: result.addedEntryId }),
                        'Ajouté à « ' + l.name + ' »');
                });
                actions.appendChild(b);
            });
            if (!lists.length) {
                var create = matchButton('Créer avec ' + m.user.name, true);
                create.addEventListener('click', function () {
                    run(api('POST', 'Watchlists/Shared', { userId: m.user.id, fromWatchlistId: sourceId, entryId: result.addedEntryId }), function (w) {
                        return 'Watchlist « ' + w.name + ' » créée, ' + m.user.name + ' est invité(e)';
                    });
                });
                actions.appendChild(create);
            }
            var ignore = matchButton('Ignorer');
            ignore.addEventListener('click', close);
            actions.appendChild(ignore);

            // Reste affichée tant que la souris ou le focus est dessus.
            row.addEventListener('mouseenter', function () { clearTimeout(timer); });
            row.addEventListener('mouseleave', arm);
            row.addEventListener('focusin', function () { clearTimeout(timer); });
            row.addEventListener('focusout', function (e) { if (!row.contains(e.relatedTarget)) arm(); });
            panel.appendChild(row);
            arm();
        });

        if (anchored) {
            positionMenu(panel, anchor);
            document.addEventListener('mousedown', onAnchoredOutside, true);
            document.addEventListener('keydown', onAnchoredKey, true);
            window.addEventListener('scroll', closeAnchored, true);
            window.addEventListener('resize', closeAnchored);
            var first = panel.querySelector('button');
            if (first) first.focus();
        }
    }

    window.__watchlistsShowMatches = showMatches;

    // Id de l'item affiché : #/details?id=... (routeur hash) ou /details?id=... (routeur classique).
    function detailItemId() {
        var h = window.location.hash;
        var q = h.indexOf('?') >= 0 ? h.slice(h.indexOf('?') + 1) : '';
        if (!/^#!?\/details/i.test(h)) {
            if (!/\/details/i.test(window.location.pathname)) return null;
            q = window.location.search.slice(1);
        }
        var id = new URLSearchParams(q).get('id');
        return id && /^[0-9a-f-]{32,36}$/i.test(id) ? id : null;
    }

    // Zone des boutons de la fiche visible (les pages précédentes restent dans le DOM, masquées).
    function detailButtonsBar() {
        var bars = document.querySelectorAll('.mainDetailButtons');
        for (var i = 0; i < bars.length; i++) {
            if (isShown(bars[i])) return bars[i];
        }
        return null;
    }

    function closeMenu() {
        if (!openMenu) return;
        openMenu.el.remove();
        document.removeEventListener('mousedown', onOutside, true);
        document.removeEventListener('keydown', onMenuKey, true);
        window.removeEventListener('scroll', closeMenu, true);
        window.removeEventListener('resize', closeMenu);
        var btn = openMenu.btn;
        openMenu = null;
        btn.setAttribute('aria-expanded', 'false');
    }

    function onOutside(e) {
        if (openMenu && !openMenu.el.contains(e.target) && !openMenu.btn.contains(e.target)) closeMenu();
    }

    function onMenuKey(e) {
        if (e.key === 'Escape' && openMenu) {
            var btn = openMenu.btn;
            e.stopPropagation();
            closeMenu();
            btn.focus();
        }
    }

    function renderDetail(btn) {
        var lists = btn.wlLists || [];
        var inLists = lists.filter(function (l) { return l.contains; });
        var icon = btn.querySelector('.wl-icon');
        btn.classList.toggle('wl-in', inLists.length > 0);
        icon.textContent = inLists.length ? 'playlist_add_check' : 'playlist_add';
        var title;
        if (lists.length === 1) {
            title = (inLists.length ? 'Retirer de « ' : 'Ajouter à « ') + lists[0].name + ' »';
        } else if (inLists.length) {
            title = 'Dans ' + inLists.map(function (l) { return '« ' + l.name + ' »'; }).join(', ');
        } else {
            title = 'Ajouter à une watchlist';
        }
        btn.title = title;
        btn.setAttribute('aria-label', title);
        if (openMenu && openMenu.btn === btn) fillMenu(openMenu.el, btn);
    }

    function loadDetail(btn) {
        btn.wlLoadedAt = Date.now();
        return api('GET', 'Watchlists/Containing/' + btn.wlItemId).then(function (lists) {
            btn.wlLists = Array.isArray(lists) ? lists : [];
            renderDetail(btn);
        }, function (e) {
            btn.wlLoadedAt = 0;
            throw e;
        });
    }

    function toggle(btn, list) {
        var req = list.contains
            ? api('DELETE', 'Watchlists/' + list.id + '/Items/' + (list.entryId || btn.wlItemId))
            : api('POST', 'Watchlists/' + list.id + '/Items', { itemId: btn.wlItemId });
        btn.disabled = true;
        return req.then(function (r) {
            toast((list.contains ? 'Retiré de « ' : 'Ajouté à « ') + list.name + ' »');
            if (!list.contains) {
                showMatches(r, list.id, function () {
                    if (btn.isConnected) loadDetail(btn).catch(function () { /* rechargé au prochain affichage */ });
                }, btn);
            }
        }, function (e) {
            toast(e.message);
        }).then(function () {
            return loadDetail(btn).catch(function () { /* l'état sera rechargé au prochain affichage */ });
        }).then(function () {
            btn.disabled = false;
            if (openMenu && openMenu.btn === btn) fillMenu(openMenu.el, btn);
        });
    }

    function createAndAdd(btn) {
        closeMenu();
        var name = window.prompt('Nom de la nouvelle watchlist');
        if (!name || !name.trim()) return;
        btn.disabled = true;
        api('POST', 'Watchlists', { name: name.trim() }).then(function (w) {
            return toggle(btn, { id: w.id, name: w.name, contains: false });
        }).catch(function (e) {
            toast(e.message);
            btn.disabled = false;
        });
    }

    function menuButton(icon, label, cls) {
        var b = document.createElement('button');
        b.type = 'button';
        b.setAttribute('role', 'menuitem');
        if (cls) b.className = cls;
        var i = document.createElement('span');
        i.className = 'material-icons';
        i.setAttribute('aria-hidden', 'true');
        i.textContent = icon;
        var n = document.createElement('span');
        n.className = 'wl-name';
        n.textContent = label;
        b.appendChild(i);
        b.appendChild(n);
        return b;
    }

    var MENU_GROUPS = [
        { key: 'private', label: 'Privées', icon: 'lock', color: '#b0b0b0' },
        { key: 'shared', label: 'Communes', icon: 'group', color: '#52b54b' },
        { key: 'public', label: 'Publiques', icon: 'public', color: '#00a4dc' }
    ];

    function groupOf(l) {
        if (l.shared) return 'shared';
        return l.visibility === 'Public' ? 'public' : 'private';
    }

    function fillMenu(menu, btn) {
        var focused = document.activeElement && menu.contains(document.activeElement) ? document.activeElement.dataset.key : null;
        menu.textContent = '';
        var title = document.createElement('div');
        title.className = 'wl-title';
        title.textContent = 'Watchlists';
        menu.appendChild(title);
        var lists = btn.wlLists || [];
        if (!lists.length) {
            var empty = document.createElement('div');
            empty.className = 'wl-empty';
            empty.textContent = 'Aucune watchlist modifiable.';
            menu.appendChild(empty);
        }
        // Groupes séparés : privées (seul membre), communes (plusieurs membres), publiques.
        MENU_GROUPS.forEach(function (g, gi) {
            var inGroup = lists.filter(function (l) { return groupOf(l) === g.key; });
            if (!inGroup.length) return;
            if (menu.querySelector('.wl-group')) {
                var gsep = document.createElement('div');
                gsep.className = 'wl-sep';
                menu.appendChild(gsep);
            }
            var head = document.createElement('div');
            head.className = 'wl-group';
            head.setAttribute('role', 'presentation');
            head.style.setProperty('--wl-group', g.color);
            head.innerHTML = '<span class="material-icons" aria-hidden="true">' + g.icon + '</span>';
            head.appendChild(document.createTextNode(g.label));
            menu.appendChild(head);
            inGroup.forEach(function (l) {
                var b = menuButton(l.contains ? 'check_box' : 'check_box_outline_blank', l.name, 'wl-in-group' + (l.contains ? ' wl-on' : ''));
                b.style.setProperty('--wl-group', g.color);
                b.setAttribute('role', 'menuitemcheckbox');
                b.setAttribute('aria-checked', String(l.contains));
                b.setAttribute('aria-label', l.name + ' (' + g.label.toLowerCase() + ')');
                b.dataset.key = l.id;
                if (g.key === 'shared' && l.visibility === 'Public') {
                    var tag = document.createElement('span');
                    tag.className = 'material-icons wl-tag';
                    tag.setAttribute('aria-hidden', 'true');
                    tag.title = 'Publique';
                    tag.textContent = 'public';
                    b.appendChild(tag);
                }
                b.addEventListener('click', function () {
                    b.disabled = true;
                    toggle(btn, l);
                });
                menu.appendChild(b);
            });
        });
        var sep = document.createElement('div');
        sep.className = 'wl-sep';
        menu.appendChild(sep);
        var create = menuButton('add', 'Nouvelle watchlist…');
        create.dataset.key = 'new';
        create.addEventListener('click', function () { createAndAdd(btn); });
        menu.appendChild(create);
        var again = focused && menu.querySelector('[data-key="' + focused + '"]');
        if (again) again.focus();
    }

    function positionMenu(menu, btn) {
        var r = btn.getBoundingClientRect();
        var w = menu.offsetWidth;
        var left = Math.max(8, Math.min(r.left + r.width / 2 - w / 2, window.innerWidth - w - 8));
        menu.style.left = left + 'px';
        menu.style.setProperty('--wl-arrow', (r.left + r.width / 2 - left) + 'px');
        // Au-dessus du bouton s'il n'y a pas la place en dessous (et plus de place au-dessus).
        // Ancré par bottom : la flèche reste sur le bouton si le contenu change de hauteur.
        var below = window.innerHeight - r.bottom - 18;
        var above = r.top - 18;
        var up = menu.offsetHeight > below && above > below;
        menu.classList.toggle('wl-above', up);
        menu.style.top = up ? 'auto' : (r.bottom + 10) + 'px';
        menu.style.bottom = up ? (window.innerHeight - r.top + 10) + 'px' : 'auto';
    }

    function showMenu(btn) {
        closeMenu();
        var menu = document.createElement('div');
        menu.className = MENU_CLASS;
        menu.setAttribute('role', 'menu');
        fillMenu(menu, btn);
        document.body.appendChild(menu);
        positionMenu(menu, btn);
        openMenu = { el: menu, btn: btn };
        btn.setAttribute('aria-expanded', 'true');
        document.addEventListener('mousedown', onOutside, true);
        document.addEventListener('keydown', onMenuKey, true);
        window.addEventListener('scroll', closeMenu, true);
        window.addEventListener('resize', closeMenu);
        var first = menu.querySelector('button');
        if (first) first.focus();
    }

    function createDetailButton(itemId) {
        // Même structure que les boutons natifs de la fiche (Lecture, Favori, ...).
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'emby-button');
        btn.className = 'button-flat detailButton emby-button ' + DETAIL_CLASS;
        btn.setAttribute('aria-haspopup', 'menu');
        btn.setAttribute('aria-expanded', 'false');
        btn.wlItemId = itemId;
        btn.disabled = true;
        var content = document.createElement('div');
        content.className = 'detailButton-content';
        var icon = document.createElement('span');
        icon.className = 'material-icons detailButton-icon wl-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = 'playlist_add';
        content.appendChild(icon);
        btn.appendChild(content);
        btn.addEventListener('click', onWatchlistClick);
        return btn;
    }

    function onWatchlistClick(e) {
        // stopPropagation : la carte (itemAction / lien) ouvrirait la fiche.
        e.preventDefault();
        e.stopPropagation();
        var btn = e.currentTarget;
        if (btn.disabled) return;
        if (openMenu && openMenu.btn === btn) { closeMenu(); return; }
        var ready = btn.wlLists ? Promise.resolve() : loadDetail(btn);
        btn.disabled = true;
        ready.then(function () {
            btn.disabled = false;
            if (btn.wlLists.length === 1) toggle(btn, btn.wlLists[0]);
            else showMenu(btn);
        }, function (err) {
            btn.disabled = false;
            toast(err.message);
        });
    }

    function createCardButton(itemId) {
        // Même structure que les boutons natifs du survol (Vu, Favori, ...).
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'cardOverlayButton cardOverlayButton-hover paper-icon-button-light ' + CARD_CLASS;
        btn.setAttribute('aria-haspopup', 'menu');
        btn.setAttribute('aria-expanded', 'false');
        btn.title = 'Ajouter à une watchlist';
        btn.wlItemId = itemId;
        var icon = document.createElement('span');
        icon.className = 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover wl-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = 'playlist_add';
        btn.appendChild(icon);
        btn.addEventListener('click', onWatchlistClick);
        // État chargé au survol (pas une requête par affiche de la bibliothèque), relu au plus toutes les 15 s.
        btn.wlHover = function () {
            if (!btn.disabled && Date.now() - (btn.wlLoadedAt || 0) > 15000) {
                loadDetail(btn).catch(function () { /* réessayé au prochain survol ou au clic */ });
            }
        };
        return btn;
    }

    function ensureCardButtons() {
        if (!client()) return;
        var bars = document.querySelectorAll('.cardOverlayContainer > .cardOverlayButton-br');
        for (var i = 0; i < bars.length; i++) {
            var bar = bars[i];
            if (bar.querySelector('.' + CARD_CLASS)) continue;
            var card = bar.closest('[data-id]');
            var type = card && card.getAttribute('data-type');
            if (type !== 'Movie' && type !== 'Series') continue;
            var btn = createCardButton(card.getAttribute('data-id'));
            var overlay = bar.parentElement;
            if (!overlay.wlHoverBound) {
                overlay.wlHoverBound = true;
                overlay.addEventListener('mouseenter', function () {
                    var b = this.querySelector('.' + CARD_CLASS);
                    if (b) b.wlHover();
                });
            }
            // En premier : React ajoute ses propres boutons après, sans déplacer celui-ci.
            bar.insertBefore(btn, bar.firstChild);
            if (overlay.matches(':hover')) btn.wlHover();
        }
    }

    // Type des items déjà consultés (évite un getItem à chaque mutation du DOM sur une fiche d'épisode, de saison...).
    var itemTypes = {};
    var pendingItem = null;

    function ensureDetailButton() {
        var c = client();
        var itemId = c && detailItemId();
        var bar = itemId && detailButtonsBar();
        document.querySelectorAll('.' + DETAIL_CLASS).forEach(function (b) {
            if (b.parentElement !== bar || b.wlItemId !== itemId) {
                if (openMenu && openMenu.btn === b) closeMenu();
                b.remove();
            }
        });
        if (!bar || bar.querySelector('.' + DETAIL_CLASS) || pendingItem === itemId) return;

        // Films et séries entières uniquement (comme l'API).
        var type = itemTypes[itemId];
        if (type !== undefined && type !== 'Movie' && type !== 'Series') return;
        pendingItem = itemId;
        var typed = type ? Promise.resolve() : c.getItem(c.getCurrentUserId(), itemId).then(function (item) {
            itemTypes[itemId] = (item && item.Type) || '';
        });
        typed.then(function () {
            var t = itemTypes[itemId];
            if (t !== 'Movie' && t !== 'Series') return;
            if (!bar.isConnected || !isShown(bar) || bar.querySelector('.' + DETAIL_CLASS) || detailItemId() !== itemId) return;
            var btn = createDetailButton(itemId);
            var more = bar.querySelector('.btnMoreCommands');
            bar.insertBefore(btn, more && more.parentElement === bar ? more : null);
            return loadDetail(btn).then(function () { btn.disabled = false; });
        }).catch(function () { /* plugin indisponible : pas de bouton */ }).then(function () {
            if (pendingItem === itemId) pendingItem = null;
        });
    }

    // ------------------------------------------------------------------ filtre « Watchlist » des bibliothèques
    // Panneau Filtres des bibliothèques Films / Séries : une case à trois états par watchlist
    // (☐ -> ✓ dans la liste -> ✗ pas dans la liste). Titres gardés : dans au moins une liste ✓ et dans aucune ✗
    // (ids = ✓ moins ✗ ; sans ✓, excludeItemIds = ✗). « Dans aucune watchlist » (exclusive) : titres absents
    // de toutes mes listes et des listes suivies.
    // Deux clients coexistent dans Jellyfin 12 :
    // - vue React (menu MUI) : titres lus par @jellyfin/sdk (axios -> XMLHttpRequest) via react-query ;
    //   on ajoute ids=... à la requête /Items, et on invalide les requêtes react-query de la bibliothèque.
    // - ancienne vue (filterDialog) : titres lus par ApiClient.getItems(query) ; on y ajoute Ids.
    // Choix mémorisé par utilisateur et par bibliothèque (localStorage), comme les filtres natifs.

    var FILTER_CLASS = 'wl-library-filter';
    var NO_MATCH_ID = '00000000000000000000000000000000';
    var listIdsCache = {};
    var NONE = 'none';
    var myListsCache = null;

    function normId(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }

    // Bibliothèque affichée : #/movies?topParentId=... (ou parentId=...).
    function libraryView() {
        var h = window.location.hash;
        var q = h.indexOf('?') >= 0 ? new URLSearchParams(h.slice(h.indexOf('?') + 1)) : null;
        var parentId = q && (q.get('parentId') || q.get('topParentId'));
        return parentId && /^[0-9a-f-]{32,36}$/i.test(parentId) ? { parentId: parentId } : null;
    }

    function filterKey(c, parentId) { return 'wl-filter-' + normId(c.getCurrentUserId()) + '-' + normId(parentId); }

    // Filtre = ids des watchlists séparés par des virgules, « !id » pour une liste exclue, ou « none » (null si aucun).
    function getFilter(c, parentId) {
        try { return localStorage.getItem(filterKey(c, parentId)); } catch (e) { return null; }
    }

    function setFilter(c, parentId, listIds) {
        try {
            if (listIds && listIds.length) localStorage.setItem(filterKey(c, parentId), listIds.join(','));
            else localStorage.removeItem(filterKey(c, parentId));
        } catch (e) { /* stockage indisponible : filtre non mémorisé */ }
    }

    // Titres (sur le serveur et visibles) d'une liste, relus au plus toutes les 30 s.
    function listItemIds(listId) {
        var cached = listIdsCache[listId];
        if (cached && Date.now() - cached.at < 30000) return cached.promise;
        var p = api('GET', 'Watchlists/' + listId).then(function (w) {
            return w.items.filter(function (i) { return i.itemId; }).map(function (i) { return normId(i.itemId); });
        });
        listIdsCache[listId] = { at: Date.now(), promise: p };
        p.catch(function () { delete listIdsCache[listId]; });
        return p;
    }

    // Mes listes + listes suivies (sans doublon, par nom), relues au plus toutes les 30 s.
    function myLists() {
        if (myListsCache && Date.now() - myListsCache.at < 30000) return myListsCache.promise;
        var p = Promise.all([api('GET', 'Watchlists'), api('GET', 'Watchlists/Followed').catch(function () { return []; })]).then(function (r) {
            return r[0].concat(r[1].filter(function (f) { return !r[0].some(function (m) { return m.id === f.id; }); }))
                .sort(function (a, b) { return a.name.localeCompare(b.name); });
        });
        myListsCache = { at: Date.now(), promise: p };
        p.catch(function () { myListsCache = null; });
        return p;
    }

    // Titres de toutes les listes du filtre (union) -> ids ; « aucune » -> exclude = titres de toutes mes listes.
    // Une liste supprimée ou inaccessible est retirée du filtre.
    function filterItemIds(c, parentId, filter) {
        if (filter === NONE) {
            return myLists().then(function (lists) {
                return Promise.all(lists.map(function (l) { return listItemIds(l.id).catch(function () { return []; }); }));
            }).then(function (r) {
                var all = {};
                r.forEach(function (ids) { ids.forEach(function (x) { all[x] = true; }); });
                return { exclude: Object.keys(all), active: true };
            });
        }
        var tokens = filter.split(',').filter(Boolean);
        return Promise.all(tokens.map(function (t) { return listItemIds(t.replace(/^!/, '')).catch(function () { return null; }); })).then(function (r) {
            var alive = tokens.filter(function (t, i) { return r[i]; });
            if (alive.length !== tokens.length) setFilter(c, parentId, alive);
            var inc = null;
            var exc = {};
            tokens.forEach(function (t, i) {
                if (!r[i]) return;
                if (t.charAt(0) === '!') {
                    r[i].forEach(function (x) { exc[x] = true; });
                } else {
                    inc = inc || {};
                    r[i].forEach(function (x) { inc[x] = true; });
                }
            });
            if (!alive.length) return { active: false };
            if (!inc) return { exclude: Object.keys(exc), active: true };
            return { ids: Object.keys(inc).filter(function (x) { return !exc[x]; }), active: true };
        });
    }

    function isLibraryQuery(query, view) {
        return !!(query && view && query.ParentId && normId(query.ParentId) === normId(view.parentId)
            && (query.IncludeItemTypes === 'Movie' || query.IncludeItemTypes === 'Series'));
    }

    function wrapGetItems() {
        var c = client();
        if (!c || typeof c.getItems !== 'function' || c.getItems.wlWrapped) return;
        var orig = c.getItems;
        var wrapped = function (userId, query) {
            var self = this;
            var args = arguments;
            var view = libraryView();
            var filter = isLibraryQuery(query, view) ? getFilter(c, view.parentId) : null;
            if (!filter) return orig.apply(self, args);
            return filterItemIds(c, view.parentId, filter).then(function (f) {
                if (!f.active) return orig.apply(self, args);
                if (f.exclude) return orig.call(self, userId, f.exclude.length ? Object.assign({}, query, { ExcludeItemIds: f.exclude.join(',') }) : query);
                return orig.call(self, userId, Object.assign({}, query, { Ids: f.ids.length ? f.ids.join(',') : NO_MATCH_ID }));
            });
        };
        wrapped.wlWrapped = true;
        c.getItems = wrapped;
    }

    // ------------------------------------------------------------------ avertissement avant suppression
    // Toutes les suppressions du client (fiche, menu d'une carte, sélection multiple) passent par ApiClient.deleteItem,
    // après la confirmation de Jellyfin. On regroupe les appels d'un même tick (sélection multiple), on demande au
    // serveur quelles watchlists (tous utilisateurs) contiennent ces titres et, s'il y en a, on redemande confirmation.
    // Annuler laisse la promesse en attente : aucune suppression, pas de message d'erreur de Jellyfin.
    // Si la vérification échoue, la suppression suit son cours (le plugin ne doit pas bloquer Jellyfin).

    var DELETE_CHECK_CHUNK = 100;
    var pendingDeletes = null;

    function wrapDeleteItem() {
        var c = client();
        var proto = c && Object.getPrototypeOf(c);
        if (!proto || typeof proto.deleteItem !== 'function' || proto.deleteItem.wlWrapped) return;
        var orig = proto.deleteItem;
        var wrapped = function (itemId) {
            var self = this;
            var args = arguments;
            if (!itemId) return orig.apply(self, args);
            return new Promise(function (resolve, reject) {
                if (!pendingDeletes) {
                    pendingDeletes = [];
                    setTimeout(flushDeletes, 0);
                }
                pendingDeletes.push({
                    id: itemId,
                    run: function () { orig.apply(self, args).then(resolve, reject); }
                });
            });
        };
        wrapped.wlWrapped = true;
        proto.deleteItem = wrapped;
    }

    function flushDeletes() {
        var batch = pendingDeletes;
        pendingDeletes = null;
        var ids = batch.map(function (d) { return normId(d.id); });
        var chunks = [];
        for (var i = 0; i < ids.length; i += DELETE_CHECK_CHUNK) {
            chunks.push(api('GET', 'Watchlists/DeleteCheck?ids=' + ids.slice(i, i + DELETE_CHECK_CHUNK).join(',')));
        }
        Promise.all(chunks).then(function (parts) {
            var hits = [].concat.apply([], parts);
            return hits.length ? confirmDelete(hits) : true;
        }, function () { return true; }).then(function (go) {
            if (go) batch.forEach(function (d) { d.run(); });
        });
    }

    function confirmDelete(hits) {
        return new Promise(function (resolve) {
            var backdrop = document.createElement('div');
            backdrop.className = 'wl-del-backdrop';
            var lists = function (h) {
                return '<ul>' + h.lists.map(function (l) {
                    var owner = l.owner && l.owner.name ? l.owner.name : '';
                    return '<li>' + (l.name
                        ? '« ' + escHtml(l.name) + ' »' + (owner ? ' <span class="wl-del-owner">(' + escHtml(owner) + ')</span>' : '')
                        : 'une liste privée' + (owner ? ' de ' + escHtml(owner) : '')) + '</li>';
                }).join('') + '</ul>';
            };
            var body = hits.length === 1
                ? '<p>« ' + escHtml(hits[0].name) + ' » est dans ' + (hits[0].lists.length > 1 ? hits[0].lists.length + ' watchlists' : 'une watchlist') + ' :</p>' + lists(hits[0])
                : '<p>' + hits.length + ' titres sont dans des watchlists :</p><ul>' + hits.map(function (h) {
                    return '<li><strong>' + escHtml(h.name) + '</strong>' + lists(h) + '</li>';
                }).join('') + '</ul>';
            backdrop.innerHTML = '<div class="wl-match-row" role="alertdialog" aria-modal="true" aria-labelledby="wl-del-title">' +
                '<h3 id="wl-del-title">Titre dans une watchlist</h3>' + body +
                '<p>Le supprimer quand même ?</p>' +
                '<div class="wl-actions"><button type="button" class="wl-primary" data-act="cancel">Annuler</button>' +
                '<button type="button" class="wl-danger" data-act="delete">Supprimer quand même</button></div></div>';
            var done = function (go) {
                document.removeEventListener('keydown', onKey, true);
                backdrop.remove();
                resolve(go);
            };
            var onKey = function (e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    e.stopPropagation();
                    done(false);
                }
            };
            backdrop.addEventListener('click', function (e) {
                if (e.target === backdrop) return done(false);
                var b = e.target.closest('button[data-act]');
                if (b) done(b.getAttribute('data-act') === 'delete');
            });
            document.addEventListener('keydown', onKey, true);
            document.body.appendChild(backdrop);
            backdrop.querySelector('[data-act="cancel"]').focus();
        });
    }

    // Requête /Items de la vue React pour la bibliothèque affichée, avec un filtre actif ?
    function libraryUrlFilter(method, url) {
        if (String(method).toUpperCase() !== 'GET') return null;
        var c = client();
        var view = libraryView();
        if (!c || !view) return null;
        var u;
        try { u = new URL(url, window.location.href); } catch (e) { return null; }
        if (!/\/Items$/i.test(u.pathname)) return null;
        var p = u.searchParams;
        var types = p.getAll('includeItemTypes').join(',').split(',').filter(Boolean);
        if (normId(p.get('parentId')) !== normId(view.parentId) || String(p.get('recursive')).toLowerCase() !== 'true'
            || types.length !== 1 || (types[0] !== 'Movie' && types[0] !== 'Series')) return null;
        var filter = getFilter(c, view.parentId);
        return filter ? { url: u, filter: filter, parentId: view.parentId, client: c } : null;
    }

    // XHR filtré : open / en-têtes différés jusqu'à ce que les ids de la liste soient connus, puis envoi.
    function patchXhr() {
        var proto = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
        if (!proto || proto.open.wlWrapped) return;
        var open = proto.open;
        var send = proto.send;
        var setHeader = proto.setRequestHeader;
        proto.open = function (method, url) {
            var f = libraryUrlFilter(method, url);
            if (!f) {
                this.wlDeferred = null;
                return open.apply(this, arguments);
            }
            this.wlDeferred = { args: Array.prototype.slice.call(arguments), filter: f, headers: [] };
        };
        proto.open.wlWrapped = true;
        proto.setRequestHeader = function () {
            if (this.wlDeferred) {
                this.wlDeferred.headers.push(arguments);
                return;
            }
            return setHeader.apply(this, arguments);
        };
        proto.send = function (body) {
            var d = this.wlDeferred;
            if (!d) return send.apply(this, arguments);
            this.wlDeferred = null;
            var xhr = this;
            filterItemIds(d.filter.client, d.filter.parentId, d.filter.filter).then(function (f) {
                if (!f.active) return;
                if (f.exclude) {
                    if (f.exclude.length) d.filter.url.searchParams.set('excludeItemIds', f.exclude.join(','));
                } else {
                    d.filter.url.searchParams.set('ids', f.ids.length ? f.ids.join(',') : NO_MATCH_ID);
                }
                d.args[1] = d.filter.url.toString();
            }).then(function () {
                open.apply(xhr, d.args);
                d.headers.forEach(function (h) { setHeader.apply(xhr, h); });
                send.call(xhr, body);
            });
        };
    }

    function escHtml(v) {
        return String(v == null ? '' : v).replace(/[&<>"']/g, function (ch) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
        });
    }

    var FILTER_STATE_LABEL = { off: '', in: ' : dans la liste', out: ' : pas dans la liste' };

    // Remplit la section : « Dans aucune watchlist » puis une case à trois états par watchlist (les miennes + suivies).
    // holder : élément retiré s'il n'y a aucune liste (la section, ou l'accordéon qui la contient).
    function fillFilterSection(section, c, view, legacy, refresh, holder) {
        holder = holder || section;
        myLists().then(function (lists) {
            if (!section.isConnected) return;
            if (!lists.length) { holder.remove(); return; }
            var state = {};
            (getFilter(c, view.parentId) || '').split(',').filter(Boolean).forEach(function (t) {
                state[normId(t.replace(/^!/, ''))] = t.charAt(0) === '!' ? 'out' : 'in';
            });
            var options = [{ id: NONE, name: 'Dans aucune watchlist' }].concat(lists);
            section.innerHTML = (legacy ? '<h3 class="checkboxListLabel">Watchlist</h3>' : '')
                + '<div class="wl-fhelp">1 clic : dans la liste · 2 clics : pas dans la liste</div>'
                + options.map(function (l) {
                    return '<button type="button" class="wl-fopt wl-chk-watchlist' + (l.id === NONE ? ' wl-none' : '') + '" data-list="' + escHtml(l.id) + '"'
                        + ' data-name="' + escHtml(l.name) + '" data-state="' + (state[normId(l.id)] || 'off') + '">'
                        + '<span class="wl-box" aria-hidden="true"></span><span class="wl-fname">' + escHtml(l.name) + '</span></button>';
                }).join('');
            var buttons = section.querySelectorAll('.wl-chk-watchlist');
            function label(b) { b.setAttribute('aria-label', b.getAttribute('data-name') + FILTER_STATE_LABEL[b.getAttribute('data-state')]); }
            buttons.forEach(function (b) {
                label(b);
                b.addEventListener('click', function (e) {
                    e.preventDefault();
                    var isNone = b.getAttribute('data-list') === NONE;
                    var cur = b.getAttribute('data-state');
                    var next = isNone ? (cur === 'off' ? 'in' : 'off') : { off: 'in', in: 'out', out: 'off' }[cur];
                    b.setAttribute('data-state', next);
                    label(b);
                    // « Aucune » exclut les listes, et inversement.
                    if (next !== 'off') {
                        buttons.forEach(function (o) {
                            if (o !== b && (isNone || o.getAttribute('data-list') === NONE)) {
                                o.setAttribute('data-state', 'off');
                                label(o);
                            }
                        });
                    }
                    var tokens = [];
                    buttons.forEach(function (o) {
                        var st = o.getAttribute('data-state');
                        if (st === 'in') tokens.push(o.getAttribute('data-list'));
                        else if (st === 'out') tokens.push('!' + o.getAttribute('data-list'));
                    });
                    setFilter(c, view.parentId, tokens);
                    refresh();
                });
            });
        }).catch(function () { holder.remove(); });
    }

    // Premières props React (en remontant depuis el) qui satisfont test.
    function findReactProps(el, test) {
        var key = el && Object.keys(el).filter(function (k) { return k.indexOf('__reactFiber$') === 0; })[0];
        for (var f = key && el[key]; f; f = f.return) {
            if (f.memoizedProps && test(f.memoizedProps)) return f.memoizedProps;
        }
        return null;
    }

    // QueryClient de react-query, retrouvé en remontant l'arbre React depuis un nœud rendu par React
    // (props.client du QueryClientProvider).
    function findQueryClient(el) {
        var p = findReactProps(el, function (q) { return q.client && typeof q.client.invalidateQueries === 'function'; });
        return p ? p.client : null;
    }

    // Le menu Filtres de Jellyfin ne connaît pas notre filtre : le point du bouton Filtres et le bouton
    // « Réinitialiser les filtres » dépendent de sa prop hasFilters (filtres natifs seulement). Quand seul
    // notre filtre est actif, on allume le point et on réactive le bouton, qui efface alors aussi notre filtre.
    // hasFilters est lu dans les props React pour ne jamais masquer ou désactiver ce que Jellyfin affiche.
    function nativeHasFilters(el) {
        var p = findReactProps(el, function (q) { return typeof q.hasFilters === 'boolean'; });
        return p ? p.hasFilters : null;
    }

    function syncMuiFilterState(c, view) {
        var ours = !!getFilter(c, view.parentId);
        document.querySelectorAll('.MuiBadge-badge.MuiBadge-dot').forEach(function (dot) {
            dot.classList.toggle('wl-badge-on', ours);
        });
        var menu = findMuiFilterMenu();
        var reset = menu && Array.prototype.filter.call(menu.children, function (el) { return el.tagName === 'BUTTON'; }).pop();
        if (!reset) return;
        if (!reset.wlReset) {
            reset.wlReset = true;
            reset.addEventListener('click', function () {
                if (!getFilter(c, view.parentId)) return;
                setFilter(c, view.parentId, null);
                menu.querySelectorAll('.wl-chk-watchlist').forEach(function (b) { b.setAttribute('data-state', 'off'); });
                refreshReactLibrary(menu);
                syncMuiFilterState(c, view);
            }, true);
        }
        if (nativeHasFilters(reset) === true) return;
        reset.disabled = !ours;
        reset.classList.toggle('Mui-disabled', !ours);
        reset.tabIndex = ours ? 0 : -1;
    }

    // Relit la bibliothèque. Jellyfin garde les requêtes « fraîches » 60 s (staleTime) : il faut les invalider,
    // « visibilitychange » (refetch au focus) ne suffit qu'au-delà. Les requêtes repassent par patchXhr.
    // Clé de la vue : ["User", userId, "Items", parentId, "ViewByType", ...] -> toute clé contenant "Items".
    function refreshReactLibrary(reactNode) {
        var qc = findQueryClient(reactNode);
        if (qc) {
            qc.invalidateQueries({ predicate: function (q) { return Array.isArray(q.queryKey) && q.queryKey.indexOf('Items') >= 0; } });
        } else {
            window.dispatchEvent(new Event('visibilitychange'));
        }
    }

    // Vue React : accordéon « Watchlist » sous « Filtres », copié d'un accordéon fermé du menu
    // (Caractéristiques, Type de vidéo...) pour en reprendre l'apparence. Le contenu d'un accordéon fermé
    // n'est pas dans le DOM (même « Filtres » à la première ouverture) : la zone dépliable est la nôtre.
    var muiAccordionOpen = null;

    // Menu Filtres de la vue React : le conteneur d'au moins 3 accordéons (Filtres, Caractéristiques, ...).
    function findMuiFilterMenu() {
        var accs = document.querySelectorAll('.MuiAccordion-root:not(.wl-mui-acc)');
        for (var i = 0; i < accs.length; i++) {
            var container = accs[i].parentNode;
            var n = 0;
            for (var j = 0; j < container.children.length; j++) {
                if (container.children[j].classList.contains('MuiAccordion-root')) n++;
            }
            if (n >= 3) return container;
        }
        return null;
    }

    function ensureMuiAccordion(container, c, view) {
        if (container.querySelector('.wl-mui-acc')) return true;
        var accs = Array.prototype.filter.call(container.children, function (el) { return el.classList.contains('MuiAccordion-root'); });
        var filtersAcc = accs[0];
        var template = accs.slice(1).filter(function (el) { return !el.classList.contains('Mui-expanded'); })[0];
        var acc = template && template.cloneNode(true);
        var summary = acc && acc.querySelector('.MuiAccordionSummary-root');
        var title = summary && summary.querySelector('.MuiAccordionSummary-content');
        if (!summary || !title) return false;

        acc.classList.add('wl-mui-acc');
        acc.classList.remove('Mui-expanded');
        // Seul l'en-tête est gardé (la zone dépliable du modèle, s'il en a une, est remplacée par la nôtre).
        Array.prototype.slice.call(acc.children).forEach(function (el) { if (!el.contains(summary)) el.remove(); });
        // Ids uniques (le modèle reste dans le menu).
        acc.querySelectorAll('[id]').forEach(function (e) { e.id = 'wl-' + e.id; });
        acc.querySelectorAll('[aria-controls]').forEach(function (e) { e.setAttribute('aria-controls', 'wl-region'); });
        while (title.firstElementChild) title = title.firstElementChild;
        title.textContent = 'Watchlist';

        var region = document.createElement('div');
        region.id = 'wl-region';
        region.setAttribute('role', 'region');
        // Même marge intérieure qu'un accordéon ouvert du menu, sinon celle de MUI par défaut.
        var detailsTpl = container.querySelector('.MuiAccordionDetails-root');
        var details = detailsTpl ? detailsTpl.cloneNode(false) : document.createElement('div');
        if (!detailsTpl) details.style.padding = '8px 16px 16px';
        var section = document.createElement('div');
        section.className = FILTER_CLASS + ' wl-mui';
        details.appendChild(section);
        region.appendChild(details);
        acc.appendChild(region);

        function setOpen(open) {
            muiAccordionOpen = open;
            [acc, summary].concat(Array.prototype.slice.call(acc.querySelectorAll('.MuiAccordionSummary-content, .MuiAccordionSummary-expandIconWrapper')))
                .forEach(function (e) { e.classList.toggle('Mui-expanded', open); });
            summary.setAttribute('aria-expanded', String(open));
            region.hidden = !open;
        }
        summary.addEventListener('click', function () { setOpen(summary.getAttribute('aria-expanded') !== 'true'); });
        summary.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                setOpen(summary.getAttribute('aria-expanded') !== 'true');
            }
        });
        // Ouvert si un filtre est actif (ou laissé ouvert), fermé sinon.
        setOpen(muiAccordionOpen === null ? !!getFilter(c, view.parentId) : muiAccordionOpen);
        container.insertBefore(acc, filtersAcc.nextSibling);
        fillFilterSection(section, c, view, false, function () {
            refreshReactLibrary(container);
            syncMuiFilterState(c, view);
        }, acc);
        return true;
    }

    function ensureFilterSection() {
        var c = client();
        var view = libraryView();
        if (!c || !view) return;

        // Vue React : menu MUI (accordéons), cases Lu / Non lu / Favori / Reprendre (value = filtre).
        var menu = findMuiFilterMenu();
        var muiFav = document.querySelector('.MuiFormGroup-root input[type="checkbox"][value="IsFavorite"]');
        var group = muiFav && muiFav.closest('.MuiFormGroup-root');
        if (!(menu && ensureMuiAccordion(menu, c, view)) && group && !group.parentNode.querySelector('.' + FILTER_CLASS)) {
            // Pas d'accordéon à copier : section sous les cases natives.
            var ms = document.createElement('div');
            ms.className = FILTER_CLASS + ' wl-mui';
            group.parentNode.insertBefore(ms, group.nextSibling);
            fillFilterSection(ms, c, view, false, function () {
                refreshReactLibrary(group);
                syncMuiFilterState(c, view);
            });
        }
        syncMuiFilterState(c, view);

        // Ancienne vue : filterDialog.
        var dlg = document.querySelector('.filterDialog');
        if (!dlg || dlg.querySelector('.' + FILTER_CLASS)) return;
        var fav = dlg.querySelector('.chkStandardFilter[data-filter="IsFavorite"]');
        var favList = fav && fav.closest('.checkboxList');
        if (!favList) return;
        var section = document.createElement('div');
        section.className = FILTER_CLASS;
        favList.parentNode.insertBefore(section, favList.nextSibling);
        var reset = dlg.querySelector('.resetFilters');
        if (reset) {
            reset.addEventListener('click', function () {
                setFilter(c, view.parentId, null);
                section.querySelectorAll('.wl-chk-watchlist').forEach(function (b) { b.setAttribute('data-state', 'off'); });
            }, true);
        }
        fillFilterSection(section, c, view, true, function () {
            // Rechargement par le panneau lui-même : « change » sur une case native, sans modifier son état.
            var native = dlg.querySelector('.chkStandardFilter');
            if (native) native.dispatchEvent(new Event('change'));
        });
    }

    var scheduled = false;
    new MutationObserver(function () {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(function () {
            scheduled = false;
            ensureButton();
            ensureDetailButton();
            ensureCardButtons();
            wrapGetItems();
            wrapDeleteItem();
            ensureFilterSection();
        });
    }).observe(document.documentElement, { childList: true, subtree: true });

    window.addEventListener('hashchange', function () {
        document.querySelectorAll('.' + NAV_CLASS).forEach(renderNav);
        refreshInvites(false);
        // Fiche remise en cache par le client : on recharge l'état (modifié entre-temps depuis la page Watchlists).
        closeMenu();
        closeAnchored();
        document.querySelectorAll('.' + DETAIL_CLASS).forEach(function (b) { b.remove(); });
    });
    ensureButton();
    ensureDetailButton();
    ensureCardButtons();
    wrapGetItems();
    wrapDeleteItem();
    patchXhr();
})();
