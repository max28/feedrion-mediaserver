using System;

namespace Feedrion.MediaServer.Core
{
    /// <summary>When a playback counts as a watch.</summary>
    public static class Scrobble
    {
        public const int DefaultThresholdPercent = 90;

        /// <summary>How far a playback went, 0–100, or null when the item's
        /// length is unknown (a stream without a runtime).</summary>
        public static double? PlayedPercent(long? positionTicks, long? runtimeTicks)
        {
            if (positionTicks == null || runtimeTicks == null || runtimeTicks <= 0) return null;
            var percent = 100.0 * positionTicks.Value / runtimeTicks.Value;
            return Math.Max(0, Math.Min(100, percent));
        }

        /// <summary>A stopped playback is a watch when it reached the reader's
        /// threshold (set in the Feedrion app), or when the server itself
        /// says it played to the end.</summary>
        public static bool CountsAsWatched(double? percent, bool playedToCompletion, int thresholdPercent)
        {
            if (playedToCompletion) return true;
            var threshold = thresholdPercent < 50 || thresholdPercent > 100 ? DefaultThresholdPercent : thresholdPercent;
            return percent.HasValue && percent.Value >= threshold;
        }
    }
}
