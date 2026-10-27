define(['loading', 'emby-input', 'emby-button'], function (loading) {
    'use strict';

    var pluginId = '8c3e1d72-4a9b-4f0e-b6d2-71a5c9e30f14';

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

    function call(method, path) {
        return ApiClient.ajax({ type: method, url: ApiClient.getUrl(path), dataType: method === 'GET' ? 'json' : null })
            .catch(function () { return null; });
    }

    var openPanels = {};

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

    function settingsPanel(l, libraries, done) {
        var userId = pick(l, 'userId');
        var addNew = !!pick(l, 'addNewShows');
        var chosen = pick(l, 'libraryIds');
        if (chosen === undefined) chosen = null;
        var panel = el('details');
        panel.style.margin = '.3em 0 .6em';
        // Re-rendered every few seconds while a code is pending: stay open.
        panel.open = !!openPanels[userId];
        panel.addEventListener('toggle', function () { openPanels[userId] = panel.open; });
        var summary = el('summary', 'Options — ' + (addNew ? 'adds new series' : 'followed series only')
            + ', ' + (chosen === null ? 'all libraries' : chosen.length + ' of ' + libraries.length + ' libraries'));
        summary.style.cursor = 'pointer';
        panel.appendChild(summary);
        var add = checkbox('Add to Feedrion the series this user watches that it does not follow yet', addNew);
        panel.appendChild(add.node);
        panel.appendChild(el('div', 'Linked libraries', 'fieldDescription'));
        var all = checkbox('All libraries, including the ones added later', chosen === null);
        panel.appendChild(all.node);
        var boxes = [];
        var list = el('div');
        list.style.marginLeft = '1.5em';
        libraries.forEach(function (lib) {
            var id = pick(lib, 'id');
            var type = pick(lib, 'type');
            var box = checkbox(pick(lib, 'name') + (type ? ' (' + type + ')' : ''),
                chosen === null || chosen.indexOf(id) >= 0);
            box.input.value = id;
            boxes.push(box.input);
            list.appendChild(box.node);
        });
        function sync() {
            boxes.forEach(function (b) { b.disabled = all.input.checked; });
        }
        all.input.addEventListener('change', sync);
        sync();
        panel.appendChild(list);
        var save = el('button');
        save.setAttribute('is', 'emby-button');
        save.className = 'raised button-alt';
        save.type = 'button';
        save.appendChild(el('span', 'Save options'));
        var note = el('span', '', 'fieldDescription');
        note.style.marginLeft = '1em';
        save.addEventListener('click', function () {
            save.disabled = true;
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Feedrion/Links/' + encodeURIComponent(userId) + '/Settings'),
                data: JSON.stringify({
                    AddNewShows: add.input.checked,
                    AllLibraries: all.input.checked,
                    LibraryIds: boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.value; }).join(',')
                }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function () {
                note.textContent = 'Saved — sent again at the next pass.';
                setTimeout(done, 1500);
            }, function () {
                note.textContent = 'Could not save.';
            }).then(function () { save.disabled = false; });
        });
        panel.appendChild(save);
        panel.appendChild(note);
        return panel;
    }

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

    return function (view) {
        var timer = null;
        var libraries = [];

        function render(links) {
            var box = view.querySelector('.feedrionLinks');
            box.textContent = '';
            (links || []).forEach(function (l) {
                var userId = pick(l, 'userId');
                var row = el('div', null, 'listItem');
                row.style.display = 'flex';
                row.style.alignItems = 'center';
                row.style.gap = '1em';
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
                body.appendChild(settingsPanel(l, libraries, refresh));
                row.appendChild(body);
                var button = el('button');
                button.setAttribute('is', 'emby-button');
                button.type = 'button';
                button.className = 'raised button-alt';
                button.appendChild(el('span', pick(l, 'linked') ? 'Unlink' : 'Link'));
                button.addEventListener('click', function () {
                    var path = 'Feedrion/Links/' + encodeURIComponent(userId);
                    (pick(l, 'linked') ? call('DELETE', path) : call('POST', path + '/Start')).then(refresh);
                });
                row.appendChild(button);
                box.appendChild(row);
            });
        }

        function refresh() {
            Promise.all([call('GET', 'Feedrion/Libraries'), call('GET', 'Feedrion/Links')]).then(function (both) {
                libraries = both[0] || [];
                var links = both[1];
                render(links);
                var waiting = (links || []).some(function (l) { return !!pick(l, 'pairingCode'); });
                clearTimeout(timer);
                if (waiting) timer = setTimeout(refresh, 3000);
            });
        }

        view.addEventListener('viewshow', function () {
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                view.querySelector('.txtPublicServerUrl').value = config.PublicServerUrl || '';
                view.querySelector('.txtSyncInterval').value = config.SyncIntervalMinutes || 15;
                view.querySelector('.txtApiBaseUrl').value = config.ApiBaseUrl || '';
                loading.hide();
            });
            refresh();
        });

        view.addEventListener('viewhide', function () { clearTimeout(timer); });

        view.querySelector('.feedrionSyncNow').addEventListener('click', function () {
            var button = this;
            var status = view.querySelector('.feedrionSyncStatus');
            button.disabled = true;
            status.textContent = 'Syncing…';
            // The server answers once the pass is over, with the links as they now are.
            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Feedrion/Sync'), dataType: 'json' })
                .then(function (links) {
                    render(links);
                    status.textContent = 'Sync finished at ' + new Date().toLocaleTimeString() + '.';
                }, function (response) {
                    var code = response && response.status ? ' (HTTP ' + response.status + ')' : '';
                    status.textContent = 'Sync failed' + code + '. See the server log.';
                    refresh();
                })
                .then(function () { button.disabled = false; });
        });

        view.querySelector('.feedrionConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.PublicServerUrl = view.querySelector('.txtPublicServerUrl').value.trim();
                config.SyncIntervalMinutes = parseInt(view.querySelector('.txtSyncInterval').value, 10) || 15;
                config.ApiBaseUrl = view.querySelector('.txtApiBaseUrl').value.trim();
                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });
    };
});
