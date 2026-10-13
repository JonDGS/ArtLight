using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArtLightControl
{
    /// <summary>
    /// Library entries that are installed and launchable but are never the thing anyone
    /// <em>played</em>, so Control must not show them as a game anywhere at all.
    ///
    /// <para>Wallpaper Engine is the one that matters. It is a Steam entry — so the scanner lists
    /// it like any other — it starts with Windows and stays running, and during a stream of the
    /// whole <em>desktop</em> rather than a game it can be the only library entry running at
    /// all. Nik, 12/10/2026: <i>"a desktop stream always come back as wallpaper engine simply
    /// because the desktop version of our app only track GAMES and not apps ... so to stop this we
    /// would have to put a blacklist on wallpaper engine, so it isnt being flagged as a game
    /// during a desktop stream."</i> Nik, 13/10/2026, after finding it still listed in the
    /// Sessions table and on the last-stream card: <i>"i dont want control to be seeing wallpaper
    /// engine at all."</i></para>
    ///
    /// <para><b>This is deliberately ONE list, consulted at every boundary.</b> The first attempt
    /// lived as a private field inside <see cref="SessionProcessMonitor"/>, which meant the
    /// wallpaper stopped being <em>credited</em> and nothing else changed: it was still a library
    /// row, still a name in session history, still on the Dashboard's last-session strip, and
    /// still on the phone. A list only one consumer reads is how that happened, so the three
    /// consumers are now the library scan (<see cref="GameLibraryService"/>), session history
    /// (<see cref="SessionLogger"/>), and the process monitor.</para>
    ///
    /// <para>Steam cannot be relied on to filter this for us: Wallpaper Engine comes back from
    /// IStoreBrowseService as an ordinary entry, so the <c>app_type</c> filter in
    /// <see cref="SteamMetadataFetcher"/> does not drop it. An explicit list is the only answer
    /// that does not depend on a store's classification.</para>
    /// </summary>
    public static class NeverTrack
    {
        /// <summary>
        /// Display names that are never a played game. Matched case-insensitively, exactly —
        /// the display name is what the library stores and what a session row records.
        /// </summary>
        private static readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase)
        {
            "Wallpaper Engine",
        };

        /// <summary>
        /// Executable names, without extension, that are never a played game. The friendly name is
        /// what a session stores, but the process monitor sees binaries and a library entry can be
        /// renamed by the store or the user — matching only the friendly name is how one slips
        /// past.
        /// </summary>
        private static readonly HashSet<string> _exeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "wallpaper32",
            "wallpaper64",
        };

        /// <summary>
        /// True when this display name belongs to something that must never be shown or credited
        /// as a game.
        /// </summary>
        public static bool IsExcludedName(string? name) =>
            !string.IsNullOrEmpty(name) && _names.Contains(name);

        /// <summary>
        /// True when this path's filename is one of the never-a-game executables.
        /// </summary>
        public static bool IsExcludedExe(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string file;
            try { file = Path.GetFileNameWithoutExtension(exePath); }
            catch { return false; }
            return !string.IsNullOrEmpty(file) && _exeNames.Contains(file);
        }

        /// <summary>
        /// True when a library entry is never a played game, by display name or by executable.
        /// One method rather than an overload, so every caller reads the same way.
        /// </summary>
        public static bool Matches(string? name, string? exePath = null) =>
            IsExcludedName(name) || IsExcludedExe(exePath);

        /// <summary>
        /// The same list applied to a list of names. Used on session history so rows written
        /// before this existed stop naming it too. Never returns null: a caller that had names
        /// and lost all of them should end up with an empty list, which is the honest answer —
        /// the monitor ran, and nothing anyone played was running.
        /// </summary>
        public static List<string> Strip(IEnumerable<string>? names)
        {
            if (names == null) return new List<string>();
            return names.Where(n => !IsExcludedName(n)).ToList();
        }
    }
}
