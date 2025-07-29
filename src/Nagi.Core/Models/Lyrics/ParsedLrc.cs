using System.Collections.Generic;
using System.Linq;

namespace Nagi.Core.Models.Lyrics {
    /// <summary>
    /// Represents a fully parsed LRC file with a collection of timed lyric lines,
    /// sorted by their start time.
    /// </summary>
    public class ParsedLrc {
        /// <summary>
        /// A sorted, read-only list of all lyric lines.
        /// </summary>
        public IReadOnlyList<LyricLine> Lines { get; }

        /// <summary>
        /// Indicates if the parsed lyrics contain any lines.
        /// </summary>
        public bool IsEmpty => !Lines.Any();

        public ParsedLrc(IEnumerable<LyricLine> lines) {
            // Ensure lines are sorted by time, which is crucial for playback syncing.
            Lines = lines.OrderBy(l => l.StartTime).ToList().AsReadOnly();
        }
    }
}