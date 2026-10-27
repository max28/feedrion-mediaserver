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
            call('GET', 'Feedrion/Links').then(function (links) {
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
