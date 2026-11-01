define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (loading) {
    'use strict';

    var pluginId = '6c2f0d5e-8a3b-4b7e-9c41-2f5d8e7a1b30';
    var PHASES = {
        idle: 'Idle', categories: 'Reading categories', movies: 'Movie categories', series: 'Series categories',
        episodes: 'Downloading episode lists', building: 'Building folders', writing: 'Writing .strm files',
        details: 'Downloading movie details', artwork: 'Downloading artwork'
    };

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }
    function fmtN(n) { return n == null ? '–' : Number(n).toLocaleString(); }
    function ago(t, now) {
        if (!t) { return '–'; }
        var d = Math.max(0, (now || Date.now() / 1000) - t);
        if (d < 60) { return Math.round(d) + 's ago'; }
        if (d < 3600) { return Math.round(d / 60) + ' min ago'; }
        if (d < 86400) { return (d / 3600).toFixed(1) + ' h ago'; }
        return (d / 86400).toFixed(1) + ' days ago';
    }
    function inT(t, now) {
        if (!t) { return '–'; }
        var d = t - now;
        if (d <= 0) { return 'soon'; }
        if (d < 3600) { return 'in ' + Math.round(d / 60) + ' min'; }
        return 'in ' + (d / 3600).toFixed(1) + ' h';
    }
    function dur(s) {
        if (s == null) { return ''; }
        s = Math.round(s);
        if (s < 90) { return s + ' s'; }
        if (s < 5400) { return Math.round(s / 60) + ' min'; }
        return (s / 3600).toFixed(1) + ' h';
    }
    function clock(t) { return t ? new Date(t * 1000).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' }) : '–'; }
    function day(t) { return t ? new Date(t * 1000).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }) : '–'; }

    function toast(msg) {
        require(['toast'], function (t) { t(msg); });
    }
    function getJson(path) { return ApiClient.getJSON(ApiClient.getUrl(path)); }
    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST', url: ApiClient.getUrl(path), data: JSON.stringify(body || {}),
            contentType: 'application/json', dataType: 'json'
        });
    }

    return function (view) {
        var $ = function (sel) { return view.querySelector(sel); };
        var $$ = function (sel) { return Array.prototype.slice.call(view.querySelectorAll(sel)); };
        var timer = null, tab = 'status', config = null, catData = null;
        var logAfter = 0, logLines = [];

        // ---- tabs ---------------------------------------------------------------
        $$('.xfs-tab').forEach(function (b) {
            b.addEventListener('click', function () {
                tab = b.getAttribute('data-tab');
                $$('.xfs-tab').forEach(function (x) { x.classList.toggle('on', x === b); });
                $$('.xfs-pane').forEach(function (p) { p.classList.toggle('on', p.getAttribute('data-pane') === tab); });
                if (tab === 'categories') { loadCats(false); }
                if (tab === 'settings' || tab === 'account') { loadConfig(); }
                if (tab === 'account') { loadAccount(false); }
                if (tab === 'log') { renderLog(); }
            });
        });
        function goTab(name) { var b = $('.xfs-tab[data-tab="' + name + '"]'); if (b) { b.click(); } }

        // ---- configuration --------------------------------------------------------
        function loadConfig() {
            return ApiClient.getPluginConfiguration(pluginId).then(function (c) {
                config = c;
                ['StrmFolder', 'SeriesOverrides', 'MovieOverrides', 'ServerUrl', 'Username', 'UserAgent'].forEach(function (k) {
                    var el = $('#' + k); if (el && document.activeElement !== el) { el.value = c[k] || ''; }
                });
                ['ApiDelay', 'ShrinkGuardPercent', 'ShrinkGuardMin'].forEach(function (k) { $('#' + k).value = c[k]; });
                ['MovieCategoryFolders', 'SeriesCategoryFolders', 'StripPrefix', 'WriteNfo', 'LockNfo', 'MovieDetails', 'MovieArtwork', 'ScanLibraryAfterRefresh']
                    .forEach(function (k) { $('#' + k).checked = !!c[k]; });
                $('#Password').value = '';
                $('#xfsPassNote').textContent = c.Password ? 'Saved. Leave blank to keep it.' : 'Not set.';
                return c;
            });
        }
        // Saved settings without touching the form, so a save never overwrites what was just typed.
        function fetchConfig() { return ApiClient.getPluginConfiguration(pluginId); }
        function saveConfig(c, msg) {
            loading.show();
            return ApiClient.updatePluginConfiguration(pluginId, c).then(function (r) {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(r);
                config = c;
                if (msg) { toast(msg); }
            }, function () { loading.hide(); toast('Saving failed.'); });
        }
        function refreshNow(quiet) {
            return postJson('xtreamfs/refresh').then(function (r) {
                if (!quiet) { toast(r.ok ? 'Refresh started.' : r.error); }
                setTimeout(loadStatus, 800);
            });
        }

        // ---- status -----------------------------------------------------------------
        function loadStatus() {
            return getJson('xtreamfs/status').then(function (d) {
                var s = d.status, now = d.now, c = d.catalog || {};
                var busy = s.phase !== 'idle' || d.running;
                var label = s.phase !== 'idle' ? (PHASES[s.phase] || s.phase) : (d.running ? 'Starting' : 'Idle');
                $('#xfsPill').textContent = label;
                $('#xfsPulse').classList.toggle('busy', busy);
                $('#xfsPhase').textContent = label;
                var pct = 0, detail = '', meta = '';
                if (s.phase !== 'idle') {
                    if (s.total) {
                        pct = Math.round(s.done * 100 / s.total);
                        if (s.phase === 'writing') { detail = pct + '%'; }
                        else { detail = fmtN(s.done) + ' of ' + fmtN(s.total) + ({ episodes: ' shows', details: ' movies', artwork: ' images' }[s.phase] || ' categories') + ' (' + pct + '%)'; }
                    }
                    var long = s.phase === 'episodes' || s.phase === 'details';
                    if (s.detail && s.phase !== 'writing') { detail += (detail ? '<br>' : '') + '<span class="xfs-muted">' + (long ? 'Now reading: ' : '') + esc(s.detail) + '</span>'; }
                    meta = 'Started ' + ago(s.started, now) + (s.eta != null && long ? ', about ' + dur(s.eta) + ' left' : '');
                } else {
                    meta = d.next_at ? 'Next scheduled refresh ' + inT(d.next_at, now) + ' (' + clock(d.next_at) + ')' : '';
                    if (s.last) { detail = lastText(s.last, now); }
                }
                $('#xfsFill').style.width = (s.phase !== 'idle' ? (s.total ? pct : 4) : 0) + '%';
                $('#xfsStrip').classList.toggle('running', busy);
                $('#xfsDetail').innerHTML = detail;
                $('#xfsMeta').textContent = meta;
                $('#xfsRefresh').disabled = busy;

                var al = '';
                if (s.shrink) {
                    al += '<div class="xfs-alert bad"><b>Refresh held back.</b> ' + esc(s.shrink) + '. If the provider really removed these, ' +
                        '<button is="emby-button" type="button" class="raised" data-action="accept"><span>Accept the smaller library</span></button></div>';
                }
                if (s.last && s.last.result === 'failed') {
                    al += '<div class="xfs-alert bad"><b>Last refresh failed</b> ' + ago(s.last.time, now) + ': ' + esc(s.last.detail) + '</div>';
                }
                $('#xfsAlerts').innerHTML = al;
                var acc = $('#xfsAlerts [data-action="accept"]');
                if (acc) { acc.addEventListener('click', acceptShrink); }

                renderChecklist(d, c);

                var st = s.strm;
                var cards = [
                    ['Shows', fmtN(c.shows), fmtN(c.episodes) + ' episodes'],
                    ['Movies', fmtN(c.movies), fmtN(c.files) + ' video files in total'],
                    ['Catalog', c.fetched ? ago(c.fetched, now) : '–', s.last && s.last.result === 'ok' ? 'last refresh took ' + dur(s.last.seconds) : ''],
                    ['Files in the folder', st ? (st.running ? 'Writing…' : (st.error ? 'Failed' : fmtN(st.files))) : '–',
                        st && !st.running && !st.error ? fmtN(st.written) + ' written, ' + fmtN(st.removed) + ' removed ' + ago(st.time, now) : (st && st.error ? esc(st.error) : '')]
                ];
                $('#xfsCards').innerHTML = cards.map(function (x) {
                    return '<div class="xfs-card"><h3>' + x[0] + '</h3><div class="xfs-big">' + x[1] + '</div><div class="xfs-muted xfs-small">' + x[2] + '</div></div>';
                }).join('');

                var rows = [];
                Object.keys(c.series_folders || {}).forEach(function (f) { rows.push([f, 'Shows', c.series_folders[f]]); });
                Object.keys(c.movie_folders || {}).forEach(function (f) { rows.push([f, 'Movies', c.movie_folders[f]]); });
                $('#xfsFolders').innerHTML = rows.length ? '<tr><th>Folder</th><th>Type</th><th class="num">Titles</th></tr>' + rows.map(function (r) {
                    return '<tr><td>' + esc(r[0]) + '</td><td class="xfs-muted">' + r[1] + '</td><td class="num">' + fmtN(r[2]) + '</td></tr>';
                }).join('') : '<tr><td class="xfs-muted">No library yet. Run a refresh once setup is complete.</td></tr>';
                var libs = d.libraries || [];
                $('#xfsLibs').textContent = libs.length
                    ? 'Emby libraries using these folders: ' + libs.map(function (l) { return l.name; }).join(', ') + '.'
                    : (d.strm_folder ? 'No Emby library points at ' + d.strm_folder + ' yet.' : '');

                if (s.changes && s.changes.length) {
                    $('#xfsChanges').innerHTML = s.changes.slice().reverse().map(function (x) {
                        var parts = [];
                        if (x.added.length) { parts.push('added ' + esc(x.added.join(', '))); }
                        if (x.removed.length) { parts.push('removed ' + esc(x.removed.join(', '))); }
                        return '<div><b>' + clock(x.time) + '</b> ' + x.kind + ': ' + parts.join('; ') + '</div>';
                    }).join('');
                }
            });
        }
        function lastText(l, now) {
            if (l.result === 'ok') { return 'Last refresh ' + ago(l.time, now) + ': ' + fmtN(l.shows) + ' shows, ' + fmtN(l.movies) + ' movies'; }
            if (l.result === 'kept previous') { return 'Last refresh ' + ago(l.time, now) + ' was held back by the shrink guard'; }
            if (l.result === 'waiting') { return esc(l.detail); }
            if (l.result === 'cancelled') { return 'Last refresh ' + ago(l.time, now) + ' was cancelled'; }
            return 'Last refresh ' + ago(l.time, now) + ' failed';
        }
        function renderChecklist(d, c) {
            var cfg = config || {};
            var steps = [
                [d.has_login, 'Add your provider login', 'account'],
                [!!(cfg.MovieCategories || cfg.SeriesCategories || cfg.MovieFolders || cfg.SeriesFolders), 'Choose categories', 'categories'],
                [!!d.strm_folder, 'Pick a folder for the .strm library', 'settings'],
                [!!c.fetched, 'Run the first refresh', null],
                [(d.libraries || []).length > 0, 'Add Emby libraries for the folders xtreamfs created', null]
            ];
            var allDone = steps.every(function (x) { return x[0]; });
            $('#xfsSetup').style.display = allDone ? 'none' : '';
            $('#xfsChecklist').innerHTML = steps.map(function (x, i) {
                var link = !x[0] && x[2] ? ' <a href="#" data-go="' + x[2] + '">Open</a>' : '';
                var tops = Object.keys(c.series_folders || {}).concat(Object.keys(c.movie_folders || {}));
                var extra = (i === 4 && !x[0] && d.strm_folder && tops.length)
                    ? '<br><span class="xfs-muted xfs-small">' + tops.map(function (f) { return esc(d.strm_folder.replace(/[\\/]+$/, '') + '/' + f); }).join(', ') +
                      '. Use Movies as the content type for movie folders and TV shows for the others.</span>' : '';
                return '<li class="' + (x[0] ? 'done' : 'todo') + '"><span class="dot">' + (x[0] ? '✓' : (i + 1)) + '</span><span>' + x[1] + extra + link + '</span></li>';
            }).join('');
            $$('#xfsChecklist [data-go]').forEach(function (a) {
                a.addEventListener('click', function (e) { e.preventDefault(); goTab(a.getAttribute('data-go')); });
            });
        }
        function acceptShrink() {
            require(['confirm'], function (confirm) {
                confirm('Titles the provider removed will disappear from the library.', 'Accept the smaller library?').then(function () {
                    postJson('xtreamfs/acceptshrink').then(function () { toast('Accepted. Refreshing.'); });
                });
            });
        }
        $('#xfsRefresh').addEventListener('click', function () { refreshNow(false); });

        // ---- log ----------------------------------------------------------------------
        function loadLog() {
            return getJson('xtreamfs/log?After=' + logAfter).then(function (d) {
                if (d.last < logAfter) { logAfter = 0; logLines = []; }
                if (d.lines.length) {
                    logLines = logLines.concat(d.lines);
                    if (logLines.length > 4000) { logLines = logLines.slice(-4000); }
                    logAfter = d.last;
                    renderRecent();
                    if (tab === 'log') { renderLog(); }
                }
            });
        }
        function lineHtml(l) { return '<div class="' + l[1] + '">' + esc(l[2]) + '</div>'; }
        function renderRecent() {
            var el = $('#xfsRecent');
            el.innerHTML = logLines.slice(-8).map(function (l) { return lineHtml([l[0], l[1], l[2].replace(/^\d{4}-\d\d-\d\d /, '')]); }).join('');
            el.scrollTop = el.scrollHeight;
        }
        function renderLog() {
            var f = ($('#xfsLogFilter').value || '').toLowerCase();
            var el = $('#xfsLog');
            el.innerHTML = logLines.filter(function (l) { return !f || l[2].toLowerCase().indexOf(f) >= 0; }).map(lineHtml).join('');
            if ($('#xfsLogFollow').checked) { el.scrollTop = el.scrollHeight; }
        }
        $('#xfsLogFilter').addEventListener('input', renderLog);

        // ---- categories -----------------------------------------------------------------
        function loadCats(live) {
            $('#xfsCatNote').textContent = live ? 'Asking the provider…' : '';
            return Promise.all([getJson('xtreamfs/categories' + (live ? '?Live=true' : '')), loadConfig()]).then(function (res) {
                var d = res[0];
                if (d.error) { $('#xfsCatNote').textContent = d.error; return; }
                catData = d;
                var nNew = ['movie', 'series'].reduce(function (a, k) { return a + d[k].rows.filter(function (r) { return r['new']; }).length; }, 0);
                $('#xfsCatNote').textContent = live ? (nNew ? nNew + ' new categor' + (nNew > 1 ? 'ies' : 'y') + ' at the provider (marked NEW).' : 'No new categories at the provider.') : '';
                var empty = !d.movie.rows.length && !d.series.rows.length;
                if (empty && !live) {
                    $('#xfsCatPanes').innerHTML = '<div class="xfs-card xfs-muted">No categories known yet. Use <b>Check the provider for new categories</b> to load them (needs your login on the Account tab).</div>';
                    return;
                }
                $('#xfsCatPanes').innerHTML = ['series', 'movie'].map(function (k) {
                    var part = d[k], label = k === 'movie' ? 'Movies' : 'TV series';
                    var rows = part.rows.slice().sort(function (a, b) { return (b['new'] - a['new']) || a.name.localeCompare(b.name); });
                    return '<div class="xfs-card"><div class="xfs-row xfs-between"><h3 style="margin:0">' + label + '</h3>' +
                        '<label class="checkboxContainer" style="margin:0"><input is="emby-checkbox" type="checkbox" class="xfs-all" data-kind="' + k + '"' + (part.all ? ' checked' : '') + ' /><span>Include all, including categories the provider adds later</span></label></div>' +
                        '<div class="xfs-tablewrap"><table><tr><th style="width:5em">Include</th><th style="width:5em">ID</th><th>Category</th><th class="num" style="width:6em">Titles</th><th class="xfs-catfolder">Own folder</th></tr>' +
                        rows.map(function (r) {
                            return '<tr><td><input type="checkbox" class="xfs-inc" data-kind="' + k + '" data-id="' + esc(r.id) + '"' + (r.included ? ' checked' : '') + ' /></td>' +
                                '<td class="xfs-muted">' + esc(r.id) + '</td><td>' + esc(r.name) + (r['new'] ? ' <span class="xfs-badge new">NEW</span>' : '') + '</td>' +
                                '<td class="num">' + fmtN(r.count) + '</td>' +
                                '<td class="xfs-catfolder"><input type="text" class="xfs-fold" data-kind="' + k + '" data-id="' + esc(r.id) + '" value="' + esc(r.folder) + '" placeholder="' + (k === 'movie' ? 'Movies' : 'TV') + '" /></td></tr>';
                        }).join('') + '</table></div></div>';
                }).join('');
            }, function () { $('#xfsCatNote').textContent = 'Could not load categories.'; });
        }
        $('#xfsLive').addEventListener('click', function () { loadCats(true); });
        // Turning on "Include all" ticks every category; untick any you want to exclude afterwards.
        $('#xfsCatPanes').addEventListener('change', function (e) {
            var t = e.target;
            if (!t.classList || !t.classList.contains('xfs-all') || !t.checked) { return; }
            $$('.xfs-inc[data-kind="' + t.getAttribute('data-kind') + '"]').forEach(function (x) { x.checked = true; });
        });
        $('#xfsCatSave').addEventListener('click', function () {
            if (!catData || !config) { return; }
            var c = config;
            ['movie', 'series'].forEach(function (k) {
                var P = k === 'movie' ? 'Movie' : 'Series';
                var allBox = $('.xfs-all[data-kind="' + k + '"]');
                var inc = $$('.xfs-inc[data-kind="' + k + '"]');
                if (allBox && allBox.checked) {
                    c[P + 'Categories'] = 'all';
                    c[P + 'Exclude'] = inc.filter(function (x) { return !x.checked; }).map(function (x) { return x.getAttribute('data-id'); }).join(',');
                } else {
                    c[P + 'Categories'] = inc.filter(function (x) { return x.checked; }).map(function (x) { return x.getAttribute('data-id'); }).join(',');
                    c[P + 'Exclude'] = '';
                }
                c[P + 'Folders'] = $$('.xfs-fold[data-kind="' + k + '"]').filter(function (x) { return x.value.trim(); })
                    .map(function (x) { return x.getAttribute('data-id') + '=' + x.value.trim().replace(/,/g, ' '); }).join(',');
            });
            if (!c.MovieCategories && !c.SeriesCategories) { toast('Select at least one category.'); return; }
            saveConfig(c, null).then(function () { refreshNow(true); toast('Saved. Refresh started.'); loadCats(false); });
        });

        // ---- settings ---------------------------------------------------------------------
        $('#xfsBrowse').addEventListener('click', function () {
            require(['directorybrowser'], function (DirectoryBrowser) {
                var picker = new DirectoryBrowser();
                picker.show({
                    header: 'Folder for the .strm library', includeFiles: false, validateWriteable: true,
                    path: $('#StrmFolder').value,
                    callback: function (path) {
                        if (path) { $('#StrmFolder').value = path; }
                        picker.close();
                    }
                });
            });
        });
        $('#xfsSettingsForm').addEventListener('submit', function (e) {
            e.preventDefault();
            fetchConfig().then(function (c) {
                var oldFolder = c.StrmFolder;
                c.StrmFolder = $('#StrmFolder').value.trim();
                c.SeriesOverrides = $('#SeriesOverrides').value.trim();
                c.MovieOverrides = $('#MovieOverrides').value.trim();
                c.ApiDelay = parseFloat($('#ApiDelay').value) || 0;
                c.ShrinkGuardPercent = parseFloat($('#ShrinkGuardPercent').value) || 0;
                c.ShrinkGuardMin = parseInt($('#ShrinkGuardMin').value, 10) || 0;
                ['MovieCategoryFolders', 'SeriesCategoryFolders', 'StripPrefix', 'WriteNfo', 'LockNfo', 'MovieDetails', 'MovieArtwork', 'ScanLibraryAfterRefresh']
                    .forEach(function (k) { c[k] = $('#' + k).checked; });
                saveConfig(c, 'Saved. The next refresh uses these settings.').then(function () {
                    loadConfig();
                    if (c.StrmFolder && c.StrmFolder !== oldFolder) { toast('Folder changed. Run a refresh to fill it.'); }
                });
            });
            return false;
        });

        // ---- account ----------------------------------------------------------------------
        function loadAccount(fresh) {
            return getJson('xtreamfs/account' + (fresh ? '?Fresh=true' : '')).then(renderAccount);
        }
        function renderAccount(a) {
            if (!a.ok) {
                $('#xfsAcctState').textContent = 'Not connected';
                $('#xfsAcctDetail').textContent = a.error || '';
                $('#xfsAcctGrid').innerHTML = '';
                return;
            }
            var now = Date.now() / 1000, days = a.expires ? Math.floor((a.expires - now) / 86400) : null;
            $('#xfsAcctState').textContent = a.status || 'Active';
            $('#xfsAcctDetail').textContent = 'Checked ' + ago(a.checked, now) + (a.trial ? ', trial account' : '');
            var cards = [
                ['Expires', a.expires ? day(a.expires) : 'Never', days == null ? 'no end date' : (days < 0 ? 'expired' : days + ' day' + (days === 1 ? '' : 's') + ' left')],
                ['Connections', a.max_connections == null ? '–' : fmtN(a.active_connections || 0) + ' of ' + fmtN(a.max_connections), 'in use now, across all your devices']
            ];
            $('#xfsAcctGrid').innerHTML = cards.map(function (x) {
                return '<div class="xfs-card"><h3>' + x[0] + '</h3><div class="xfs-big">' + esc(x[1]) + '</div><div class="xfs-muted xfs-small">' + esc(x[2]) + '</div></div>';
            }).join('');
        }
        function loginBody() {
            return { ServerUrl: $('#ServerUrl').value.trim(), Username: $('#Username').value.trim(), Password: $('#Password').value };
        }
        function note(msg, ok) { var n = $('#xfsLoginNote'); n.textContent = msg; n.className = 'xfs-note ' + (ok ? 'ok' : 'bad'); }
        $('#xfsAcctCheck').addEventListener('click', function () { loadAccount(true); });
        $('#xfsTest').addEventListener('click', function () {
            note('Testing…', true);
            postJson('xtreamfs/login/test', loginBody()).then(function (r) {
                if (r.ok) { note('Login works: ' + (r.status || 'Active') + ' account' + (r.max_connections ? ', ' + r.max_connections + ' connections' : '') + '.', true); renderAccount(r); }
                else { note(r.error, false); }
            }, function () { note('The test failed to run.', false); });
        });
        $('#xfsLoginForm').addEventListener('submit', function (e) {
            e.preventDefault();
            var body = loginBody(), agent = $('#UserAgent').value.trim();
            note('Testing and saving…', true);
            postJson('xtreamfs/login/test', body).then(function (r) {
                var save = function () {
                    fetchConfig().then(function (c) {
                        var changed = c.ServerUrl !== body.ServerUrl || c.Username !== body.Username || (body.Password && body.Password !== c.Password);
                        c.ServerUrl = body.ServerUrl; c.Username = body.Username;
                        if (body.Password) { c.Password = body.Password; }
                        c.UserAgent = agent || 'VLC/3.0.20 LibVLC/3.0.20';
                        saveConfig(c, null).then(function () {
                            $('#Password').value = '';
                            note(changed ? 'Saved. Refresh started, because stream links include the login.' : 'Saved.', true);
                            if (changed && c.StrmFolder) { refreshNow(true); }
                            loadAccount(true);
                        });
                    });
                };
                if (r.ok) { save(); return; }
                require(['confirm'], function (confirm) {
                    confirm('The test failed: ' + r.error, 'Save this login anyway?').then(save, function () { note(r.error, false); });
                });
            });
            return false;
        });

        // ---- polling ------------------------------------------------------------------------
        function tick() { loadStatus(); loadLog(); }
        view.addEventListener('viewshow', function () {
            loadConfig().then(tick);
            if (timer) { clearInterval(timer); }
            timer = setInterval(tick, 2000);
        });
        view.addEventListener('viewhide', function () {
            if (timer) { clearInterval(timer); timer = null; }
        });
    };
});
