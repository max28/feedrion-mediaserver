define(['loading', 'emby-input', 'emby-button', 'emby-select'], function (loading) {
    'use strict';

    var pluginId = '8c3e1d72-4a9b-4f0e-b6d2-71a5c9e30f14';

    // Emby binds the request body onto its DTOs: the lists travel as
    // comma-separated strings.
    function settingsBody(addNewShows, allLibraries, ids) {
        return { AddNewShows: addNewShows, AllLibraries: allLibraries, LibraryIds: ids.join(',') };
    }

    function ignoredBody(ids) {
        return { TvdbIds: ids.join(',') };
    }

    return function (view) {
        var timer = null;
        var links = [];
        var libraries = [];
        var series = [];
        var ignored = [];
        var selectedUser = null;
        var optionsFor = null;

        // Emby's serializer may keep the C# property names: read both spellings.
        function pick(o, camel) {
            var pascal = camel.charAt(0).toUpperCase() + camel.slice(1);
            return o[camel] !== undefined ? o[camel] : o[pascal];
        }

        function el(tag, text, cls) {
            var node = document.createElement(tag);
            if (text) node.textContent = text;
            if (cls) node.className = cls;
            return node;
        }

        function q(selector) {
            return view.querySelector(selector);
        }

        function request(method, path, body) {
            var options = { type: method, url: ApiClient.getUrl(path), dataType: method === 'DELETE' ? null : 'json' };
            if (body !== undefined) {
                options.data = JSON.stringify(body);
                options.contentType = 'application/json';
            }
            return ApiClient.ajax(options);
        }

        function call(method, path) {
            return request(method, path).catch(function () { return null; });
        }

        function button(text, onClick) {
            var b = el('button');
            b.setAttribute('is', 'emby-button');
            b.type = 'button';
            b.className = 'raised button-alt';
            b.appendChild(el('span', text));
            b.addEventListener('click', onClick);
            return b;
        }

        function checkbox(label, checked) {
            var wrap = el('label');
            wrap.style.display = 'block';
            wrap.style.margin = '.3em 0';
            var input = el('input');
            input.type = 'checkbox';
            input.checked = !!checked;
            input.style.marginRight = '.5em';
            wrap.appendChild(input);
            wrap.appendChild(document.createTextNode(label));
            return { node: wrap, input: input };
        }

        function row() {
            var r = el('div', null, 'listItem');
            r.style.display = 'flex';
            r.style.alignItems = 'center';
            r.style.gap = '1em';
            r.style.padding = '.4em 0';
            return r;
        }

        function findLink(userId) {
            for (var i = 0; i < links.length; i++) if (pick(links[i], 'userId') === userId) return links[i];
            return null;
        }

        // ── Options, per user ────────────────────────────────────────────────────

        function userLabel(l) {
            var name = pick(l, 'userName') || pick(l, 'userId');
            if (!pick(l, 'linked')) return name + ' — not linked';
            var who = pick(l, 'feedrionName');
            return name + ' — linked' + (who ? ' to ' + who : '');
        }

        function renderUserPicker() {
            var select = q('.feedrionOptionsUser');
            if (!selectedUser || !findLink(selectedUser)) {
                var first = links.filter(function (l) { return pick(l, 'linked'); })[0] || links[0];
                selectedUser = first ? pick(first, 'userId') : null;
            }
            select.textContent = '';
            links.forEach(function (l) {
                var option = el('option', userLabel(l));
                option.value = pick(l, 'userId');
                select.appendChild(option);
            });
            if (selectedUser) select.value = selectedUser;
        }

        function renderOptions() {
            var box = q('.feedrionOptions');
            box.textContent = '';
            optionsFor = selectedUser;
            var link = selectedUser ? findLink(selectedUser) : null;
            if (!link) {
                box.appendChild(el('div', 'No user on this server.', 'fieldDescription'));
                return;
            }
            var chosen = pick(link, 'libraryIds');
            if (chosen === undefined) chosen = null;
            var add = checkbox('Add to Feedrion the series this user watches that it does not follow yet',
                pick(link, 'addNewShows'));
            box.appendChild(add.node);
            box.appendChild(el('div', 'Off: only the series already in « Mes séries » get their episodes marked watched.',
                'fieldDescription'));
            var title = el('div', 'Linked libraries', 'inputLabel');
            title.style.marginTop = '1em';
            box.appendChild(title);
            var all = checkbox('All libraries, including the ones added later', chosen === null);
            box.appendChild(all.node);
            var list = el('div');
            list.style.marginLeft = '1.5em';
            var boxes = [];
            libraries.forEach(function (lib) {
                var id = pick(lib, 'id');
                var type = pick(lib, 'type');
                var b = checkbox(pick(lib, 'name') + (type ? ' (' + type + ')' : ''), chosen === null || chosen.indexOf(id) >= 0);
                b.input.value = id;
                boxes.push(b.input);
                list.appendChild(b.node);
            });
            function sync() {
                boxes.forEach(function (b) { b.disabled = all.input.checked; });
            }
            all.input.addEventListener('change', sync);
            sync();
            box.appendChild(list);
            var actions = el('div');
            actions.style.marginTop = '.6em';
            var note = el('span', '', 'fieldDescription');
            note.style.marginLeft = '1em';
            var save = button('Save options', function () {
                var ids = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.value; });
                save.disabled = true;
                request('POST', 'Feedrion/Links/' + encodeURIComponent(pick(link, 'userId')) + '/Settings',
                    settingsBody(add.input.checked, all.input.checked, ids))
                    .then(function (updated) {
                        if (updated) {
                            links = links.map(function (l) { return pick(l, 'userId') === pick(updated, 'userId') ? updated : l; });
                        }
                        note.textContent = 'Saved — sent again at the next pass.';
                    }, function () {
                        note.textContent = 'Could not save.';
                    })
                    .then(function () { save.disabled = false; });
            });
            actions.appendChild(save);
            actions.appendChild(note);
            box.appendChild(actions);
        }

        // ── Users ────────────────────────────────────────────────────────────────

        function statusText(l) {
            var code = pick(l, 'pairingCode');
            if (code) return 'Code: ' + code + ' — type it in the Feedrion app.';
            if (!pick(l, 'linked')) return pick(l, 'lastError') === 'revoked' ? 'Disconnected from the app.' : 'Not linked.';
            var name = pick(l, 'feedrionName');
            var who = name ? 'Linked to ' + name : 'Linked';
            if (pick(l, 'premium') === false) return who + ' — Premium required, nothing is exchanged.';
            if (pick(l, 'lastError')) return who + ' — last pass: ' + pick(l, 'lastError');
            var at = pick(l, 'lastSyncAt');
            return who + (at ? ' — synced ' + new Date(at).toLocaleString() : '');
        }

        function renderLinks() {
            var box = q('.feedrionLinks');
            box.textContent = '';
            links.forEach(function (l) {
                var userId = pick(l, 'userId');
                var r = row();
                var body = el('div');
                body.style.flex = '1';
                body.appendChild(el('div', pick(l, 'userName') || userId, 'listItemBodyText'));
                body.appendChild(el('div', statusText(l), 'secondary listItemBodyText'));
                var url = pick(l, 'pairingUrl');
                if (url) {
                    var a = el('a', url);
                    a.href = url;
                    a.target = '_blank';
                    a.rel = 'noopener noreferrer';
                    body.appendChild(a);
                }
                r.appendChild(body);
                r.appendChild(button(pick(l, 'linked') ? 'Unlink' : 'Link', function () {
                    var path = 'Feedrion/Links/' + encodeURIComponent(userId);
                    (pick(l, 'linked') ? call('DELETE', path) : call('POST', path + '/Start')).then(refresh);
                }));
                box.appendChild(r);
            });
        }

        function refresh() {
            Promise.all([call('GET', 'Feedrion/Libraries'), call('GET', 'Feedrion/Links')]).then(function (both) {
                libraries = both[0] || [];
                links = both[1] || [];
                renderLinks();
                renderUserPicker();
                // Not redrawn by the pairing poll: it would undo unsaved ticks.
                if (optionsFor !== selectedUser) renderOptions();
                var waiting = links.some(function (l) { return !!pick(l, 'pairingCode'); });
                clearTimeout(timer);
                if (waiting) timer = setTimeout(refresh, 3000);
            });
        }

        // ── Ignored series ───────────────────────────────────────────────────────

        function seriesName(id) {
            for (var i = 0; i < series.length; i++) {
                if (pick(series[i], 'tvdbId') === id) {
                    var year = pick(series[i], 'year');
                    return pick(series[i], 'name') + (year ? ' (' + year + ')' : '');
                }
            }
            return 'TheTVDB ' + id + ' (no longer on this server)';
        }

        function saveIgnored(ids) {
            var status = q('.feedrionIgnoredStatus');
            status.textContent = 'Saving…';
            request('POST', 'Feedrion/IgnoredSeries', ignoredBody(ids)).then(function (saved) {
                ignored = (saved || ids).map(Number);
                renderIgnored();
                status.textContent = 'Saved.';
            }, function () {
                status.textContent = 'Could not save.';
            });
        }

        function renderIgnored() {
            var box = q('.feedrionIgnored');
            box.textContent = '';
            if (ignored.length === 0) box.appendChild(el('div', 'No series ignored.', 'fieldDescription'));
            ignored.slice().sort(function (a, b) {
                return seriesName(a).localeCompare(seriesName(b));
            }).forEach(function (id) {
                var r = row();
                var name = el('div', seriesName(id), 'listItemBodyText');
                name.style.flex = '1';
                r.appendChild(name);
                r.appendChild(button('Stop ignoring', function () {
                    saveIgnored(ignored.filter(function (x) { return x !== id; }));
                }));
                box.appendChild(r);
            });
            renderSeriesResults();
        }

        function renderSeriesResults() {
            var text = q('.feedrionSeriesFilter').value.trim().toLowerCase();
            var box = q('.feedrionSeriesResults');
            box.textContent = '';
            if (!text) return;
            var hits = series.filter(function (s) {
                return ignored.indexOf(pick(s, 'tvdbId')) < 0 && String(pick(s, 'name')).toLowerCase().indexOf(text) >= 0;
            });
            if (hits.length === 0) {
                box.appendChild(el('div', 'No other series of this server matches.', 'fieldDescription'));
                return;
            }
            hits.slice(0, 30).forEach(function (s) {
                var id = pick(s, 'tvdbId');
                var r = row();
                var name = el('div', seriesName(id), 'listItemBodyText');
                name.style.flex = '1';
                r.appendChild(name);
                r.appendChild(button('Ignore', function () {
                    saveIgnored(ignored.concat([id]));
                }));
                box.appendChild(r);
            });
            if (hits.length > 30) box.appendChild(el('div', (hits.length - 30) + ' more — type more of the name.', 'fieldDescription'));
        }

        // ── Wiring ───────────────────────────────────────────────────────────────

        view.addEventListener('viewshow', function () {
            loading.show();
            optionsFor = null;
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                q('.txtPublicServerUrl').value = config.PublicServerUrl || '';
                q('.txtSyncInterval').value = config.SyncIntervalMinutes || 15;
                q('.txtApiBaseUrl').value = config.ApiBaseUrl || '';
                ignored = (config.IgnoredTvdbSeriesIds || []).map(Number);
                loading.hide();
                call('GET', 'Feedrion/Series').then(function (list) {
                    series = list || [];
                    renderIgnored();
                });
                renderIgnored();
            });
            refresh();
        });

        view.addEventListener('viewhide', function () { clearTimeout(timer); });

        q('.feedrionOptionsUser').addEventListener('change', function () {
            selectedUser = this.value;
            renderOptions();
        });

        q('.feedrionSeriesFilter').addEventListener('input', renderSeriesResults);

        q('.feedrionSyncNow').addEventListener('click', function () {
            var b = this;
            var status = q('.feedrionSyncStatus');
            b.disabled = true;
            status.textContent = 'Syncing…';
            // The server answers once the pass is over, with the links as they now are.
            request('POST', 'Feedrion/Sync').then(function (result) {
                links = result || [];
                renderLinks();
                renderUserPicker();
                status.textContent = 'Sync finished at ' + new Date().toLocaleTimeString() + '.';
            }, function (response) {
                var code = response && response.status ? ' (HTTP ' + response.status + ')' : '';
                status.textContent = 'Sync failed' + code + '. See the server log.';
                refresh();
            }).then(function () { b.disabled = false; });
        });

        q('.feedrionConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.PublicServerUrl = q('.txtPublicServerUrl').value.trim();
                config.SyncIntervalMinutes = parseInt(q('.txtSyncInterval').value, 10) || 15;
                config.ApiBaseUrl = q('.txtApiBaseUrl').value.trim();
                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });
    };
});
