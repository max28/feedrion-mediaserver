using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Feedrion
{
    /// <summary>What the configuration page calls. Administrators only: linking a
    /// server user to a Feedrion account is the server owner's decision, and the
    /// code is then confirmed by that user in their own Feedrion app.</summary>
    [ApiController]
    [Route("Feedrion")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class FeedrionController : ControllerBase
    {
        private readonly SyncEngine _engine;
        private readonly IUserManager _users;
        private readonly IServerApplicationHost _host;

        public FeedrionController(SyncEngine engine, IUserManager users, IServerApplicationHost host)
        {
            _engine = engine;
            _users = users;
            _host = host;
        }

        [HttpGet("Links")]
        public ActionResult<IEnumerable<LinkView>> GetLinks()
        {
            var links = _engine.Links.ToDictionary(l => l.ServerUserId, StringComparer.OrdinalIgnoreCase);
            return _users.GetUsers()
                .Select(u =>
                {
                    var key = JellyfinAdapter.UserKey(u.Id);
                    links.TryGetValue(key, out var link);
                    return LinkView.From(key, u.Username, link, _engine.GetPending(key));
                })
                .OrderBy(v => v.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [HttpPost("Links/{userId}/Start")]
        public async Task<ActionResult<LinkView>> StartLink([FromRoute] string userId, CancellationToken ct)
        {
            if (!Guid.TryParse(userId, out var id) || _users.GetUserById(id) is not { } user)
                return NotFound();
            var key = JellyfinAdapter.UserKey(user.Id);
            try
            {
                var pending = await _engine.StartLinkAsync(key, user.Username, "Jellyfin · " + _host.FriendlyName, ct)
                    .ConfigureAwait(false);
                return LinkView.From(key, user.Username, _engine.Links.FirstOrDefault(l => l.ServerUserId == key), pending);
            }
            catch (FeedrionApiException e)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { detail = e.Detail ?? "feedrion_unreachable" });
            }
        }

        /// <summary>The server's libraries, for the per-user choice.</summary>
        [HttpGet("Libraries")]
        public ActionResult<IEnumerable<MediaLibrary>> GetLibraries() => _engine.ListLibraries().ToList();

        /// <summary>What a server user's link sends: which libraries, and
        /// whether a series not followed yet in Feedrion gets added.</summary>
        [HttpPost("Links/{userId}/Settings")]
        public ActionResult<LinkView> UpdateSettings([FromRoute] string userId, [FromBody] LinkSettings settings)
        {
            if (!Guid.TryParse(userId, out var id) || _users.GetUserById(id) is not { } user)
                return NotFound();
            var key = JellyfinAdapter.UserKey(user.Id);
            var state = _engine.UpdateSettings(key, user.Username, settings ?? new LinkSettings());
            return LinkView.From(key, user.Username, state, _engine.GetPending(key));
        }

        [HttpDelete("Links/{userId}")]
        public async Task<ActionResult> Unlink([FromRoute] string userId, CancellationToken ct)
        {
            if (!Guid.TryParse(userId, out var id)) return NotFound();
            await _engine.UnlinkAsync(JellyfinAdapter.UserKey(id), ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>Runs a pass now and answers once it is over, with the links
        /// as they now are — the page has something to show.</summary>
        [HttpPost("Sync")]
        public async Task<ActionResult<IEnumerable<LinkView>>> Sync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await _engine.SyncNowAsync(timeout.Token).ConfigureAwait(false);
            return GetLinks();
        }
    }
}
