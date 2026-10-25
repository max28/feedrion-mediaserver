using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller.Net;
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

    [Route("/Feedrion/Sync", "POST", Summary = "Run a sync pass now")]
    public sealed class SyncFeedrion : IReturnVoid
    {
    }

    /// <summary>What the configuration page calls — administrators only, like
    /// the Jellyfin plugin's controller.</summary>
    [Authenticated(Roles = "Admin")]
    public sealed class FeedrionApi : IService
    {
        private static FeedrionEntryPoint Entry =>
            FeedrionEntryPoint.Instance ?? throw new InvalidOperationException("Feedrion is not running.");

        public object Get(GetFeedrionLinks request)
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

        public void Post(SyncFeedrion request) => Entry.Engine.RequestSync();
    }
}
