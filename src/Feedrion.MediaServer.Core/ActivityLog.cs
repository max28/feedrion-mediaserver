using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feedrion.MediaServer.Core
{
    /// <summary>One change exchanged with Feedrion, as the configuration page
    /// shows it. Names are the server's own and stay on the server: this is
    /// written to the plugin's data folder and never sent anywhere.</summary>
    public sealed class ActivityEntry
    {
        public const string Watched = "watched";
        public const string Unwatched = "unwatched";

        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }

        /// <summary>True: this server told Feedrion. False: Feedrion's watch,
        /// marked played here.</summary>
        [JsonPropertyName("toFeedrion")] public bool ToFeedrion { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; } = Watched;
        [JsonPropertyName("userName")] public string? UserName { get; set; }
        [JsonPropertyName("series")] public string? Series { get; set; }
        [JsonPropertyName("episodeName")] public string? EpisodeName { get; set; }
        [JsonPropertyName("season")] public int? Season { get; set; }
        [JsonPropertyName("episode")] public int? Episode { get; set; }
        [JsonPropertyName("tvdbSeriesId")] public int TvdbSeriesId { get; set; }
    }

    /// <summary>The last <see cref="Keep"/> changes, newest last, in one JSON
    /// file written atomically like the links. A damaged file starts empty.</summary>
    public sealed class ActivityLog
    {
        public const int Keep = 50;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _path;
        private readonly object _gate = new object();
        private readonly List<ActivityEntry> _entries;

        public ActivityLog(string path)
        {
            _path = path;
            _entries = new List<ActivityEntry>();
            try
            {
                if (File.Exists(path))
                {
                    var read = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(path, Encoding.UTF8), Json);
                    if (read != null) _entries.AddRange(read.Skip(Math.Max(0, read.Count - Keep)));
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                // A history is not worth refusing to start over.
            }
        }

        /// <summary>Newest first.</summary>
        public IReadOnlyList<ActivityEntry> Latest()
        {
            lock (_gate) return Enumerable.Reverse(_entries).ToList();
        }

        public void Add(IEnumerable<ActivityEntry> entries)
        {
            lock (_gate)
            {
                var before = _entries.Count;
                _entries.AddRange(entries);
                if (_entries.Count == before) return;
                if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json), Encoding.UTF8);
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(temp, _path);
                }
                catch (IOException)
                {
                    // Kept in memory; the next write tries again.
                }
            }
        }
    }
}
