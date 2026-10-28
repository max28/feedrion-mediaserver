using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Plugin.Feedrion
{
    [Route("/Feedrion/Links", "GET", Summary = "The server users and their Feedrion link")]
    public sealed class GetFeedrionLinks : IReturn<List<LinkView>>
    {
    }

    [Route("/Feedrion/Links/{UserId}/Start", "POST", Summary = "Ask Feedrion for a pairing code")]
    public sealed class StartFeedrionLink : IReturn<LinkView>
    {
        public string UserId { get; set; } = "";
    }

    [Route("/Feedrion/Links/{UserId}", "DELETE", Summary = "Unlink a user")]
    public sealed class DeleteFeedrionLink : IReturnVoid
    {
        public string UserId { get; set; } = "";
    }

    [Route("/Feedrion/Libraries", "GET", Summary = "The server's libraries")]
    public sealed class GetFeedrionLibraries : IReturn<List<MediaLibrary>>
    {
    }

    /// <summary>A user's link settings. The libraries travel as one comma-separated
    /// string (Emby's request binding), with [AllLibraries] for « every one ».</summary>
    [Route("/Feedrion/Links/{UserId}/Settings", "POST", Summary = "Set what a user's link sends")]
    public sealed class UpdateFeedrionLinkSettings : IReturn<LinkView>
    {
        public string UserId { get; set; } = "";
        public bool AddNewShows { get; set; }
        public bool AllLibraries { get; set; } = true;
        public bool NeverUnwatch { get; set; } = true;
        public string LibraryIds { get; set; } = "";
    }

    [Route("/Feedrion/Activity", "GET", Summary = "The last changes exchanged with Feedrion")]
    public sealed class GetFeedrionActivity : IReturn<List<ActivityEntry>>
    {
    }

    [Route("/Feedrion/Series", "GET", Summary = "The server's series with a TheTVDB id")]
    public sealed class GetFeedrionSeries : IReturn<List<MediaSeries>>
    {
    }

    /// <summary>The ignored series, as one comma-separated string of TheTVDB ids.</summary>
    [Route("/Feedrion/IgnoredSeries", "POST", Summary = "Replace the ignored series")]
    public sealed class SetFeedrionIgnoredSeries : IReturn<int[]>
    {
        public string TvdbIds { get; set; } = "";
    }

    [Route("/Feedrion/Sync", "POST", Summary = "Run a sync pass now and return the links")]
    public sealed class SyncFeedrion : IReturn<List<LinkView>>
    {
    }

    /// <summary>What the configuration page calls — administrators only, like
    /// the Jellyfin plugin's controller.</summary>
    [Authenticated(Roles = "Admin")]
    public sealed class FeedrionApi : IService
    {
        // Long enough for a first pass over a big library; the page waits for it.
        private static readonly TimeSpan SyncNowTimeout = TimeSpan.FromMinutes(2);

        private readonly ISessionManager _sessions;
        private readonly IUserDataManager _userData;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IServerApplicationHost _host;
        private readonly ILogManager _logs;

        public FeedrionApi(
            ISessionManager sessions, IUserDataManager userData, ILibraryManager library,
            IUserManager users, IServerApplicationHost host, ILogManager logs)
        {
            _sessions = sessions;
            _userData = userData;
            _library = library;
            _users = users;
            _host = host;
            _logs = logs;
        }

        private FeedrionEntryPoint Entry =>
            FeedrionEntryPoint.Ensure(_sessions, _userData, _library, _users, _host, _logs);

        public object Get(GetFeedrionLinks request) => ListLinks();

        private List<LinkView> ListLinks()
        {
            var links = Entry.Engine.Links.ToDictionary(l => l.ServerUserId, StringComparer.OrdinalIgnoreCase);
            return Entry.Users.GetUserList(new MediaBrowser.Model.Querying.UserQuery { IsDisabled = false })
                .Select(u =>
                {
                    var key = EmbyAdapter.UserKey(u);
                    links.TryGetValue(key, out var link);
                    return LinkView.From(key, u.Name, link, Entry.Engine.GetPending(key));
                })
                .OrderBy(v => v.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public object Get(GetFeedrionLibraries request) => Entry.Engine.ListLibraries().ToList();

        public object Get(GetFeedrionSeries request) => Entry.Engine.ListSeries().ToList();

        public object Get(GetFeedrionActivity request) => Entry.Engine.Activity().ToList();

        public object Post(SetFeedrionIgnoredSeries request)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Feedrion plugin not loaded.");
            var engine = Entry.Engine;
            plugin.Configuration.IgnoredTvdbSeriesIds = (request.TvdbIds ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(raw => int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
            plugin.SaveConfiguration();
            engine.IgnoredSeriesChanged();
            return plugin.Configuration.IgnoredTvdbSeriesIds;
        }

        public object Post(UpdateFeedrionLinkSettings request)
        {
            var user = Entry.Adapter.FindUser(request.UserId) ?? throw new ArgumentException("Unknown user.");
            var key = EmbyAdapter.UserKey(user);
            var state = Entry.Engine.UpdateSettings(key, user.Name, new LinkSettings
            {
                AddNewShows = request.AddNewShows,
                NeverUnwatch = request.NeverUnwatch,
                LibraryIds = request.AllLibraries
                    ? null
                    : (request.LibraryIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            });
            return LinkView.From(key, user.Name, state, Entry.Engine.GetPending(key));
        }

        public async Task<object> Post(StartFeedrionLink request)
        {
            var user = Entry.Adapter.FindUser(request.UserId) ?? throw new ArgumentException("Unknown user.");
            var key = EmbyAdapter.UserKey(user);
            var pending = await Entry.Engine
                .StartLinkAsync(key, user.Name, "Emby · " + Entry.Host.FriendlyName, CancellationToken.None)
                .ConfigureAwait(false);
            return LinkView.From(key, user.Name, Entry.Engine.Links.FirstOrDefault(l => l.ServerUserId == key), pending);
        }

        public async Task Delete(DeleteFeedrionLink request)
        {
            await Entry.Engine.UnlinkAsync(request.UserId, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<object> Post(SyncFeedrion request)
        {
            using (var timeout = new CancellationTokenSource(SyncNowTimeout))
            {
                await Entry.Engine.SyncNowAsync(timeout.Token).ConfigureAwait(false);
            }
            return ListLinks();
        }
    }
}
