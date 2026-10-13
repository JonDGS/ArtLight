using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtLightControl
{
    public class GameLibraryEntry
    {
        public string Name { get; set; } = "";
        public string Store { get; set; } = "";
        public string? SteamAppId { get; set; }

        /// <summary>Store-specific identifier used for cover art lookup and stable deduplication.</summary>
        public string? StoreId { get; set; }

        /// <summary>
        /// If false, this game is excluded from the Sunshine sync (but remains visible in the list).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Set to true when the user manually edits the game name, preventing auto-correction
        /// from the Steam Store API on subsequent syncs.
        /// </summary>
        public bool UserRenamed { get; set; } = false;

        /// <summary>
        /// True for games added manually by the user (not discovered by a store scanner).
        /// Manual games are preserved across automatic syncs and cleared by Clear Sync.
        /// </summary>
        public bool IsManual { get; set; } = false;

        /// <summary>
        /// Full path to the game executable. Only persisted for manual games;
        /// auto-discovered games re-resolve their ExePath on each scan.
        /// </summary>
        public string? ExePath { get; set; }

        /// <summary>
        /// Explicit process name for Xbox Game Pass / UWP games (e.g. "Forza Motorsport").
        /// Populated by the Xbox scanner or set manually by the user.
        /// Takes priority over the display Name in process-name matching.
        /// </summary>
        public string? ProcessName { get; set; }

        /// <summary>
        /// Root installation directory. Populated only for Battle.net games and used by
        /// SessionProcessMonitor for directory-based process matching (since all Battle.net
        /// games share the same client launcher and cannot be identified by exe name alone).
        /// </summary>
        public string? InstallDir { get; set; }

        /// <summary>
        /// Store-specific launch id, when the store's own launcher has to do the launching.
        /// Epic uses "&lt;namespace&gt;:&lt;catalogItemId&gt;:&lt;appName&gt;" — its games cannot be started
        /// by running their exe, whatever else is running, because the entitlement and Epic
        /// Online Services tokens arrive on the command line from the launcher.
        /// </summary>
        public string? LaunchId { get; set; }

        // ── UI helpers (not persisted) ────────────────────────────────────────

        private static readonly string CoverCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArtLightControl", "covers");

        /// <summary>
        /// Absolute path to the cached cover PNG for this game, or null if not yet downloaded.
        /// Uses the same naming convention as CoverArtFetcher / StoreCoverFetcher.
        /// </summary>
        [JsonIgnore]
        public string? CoverImagePath
        {
            get
            {
                string? fileName = GetCoverFileName();
                if (fileName == null) return null;
                string path = Path.Combine(CoverCacheDir, fileName);
                return File.Exists(path) ? path : null;
            }
        }

        private string? GetCoverFileName()
        {
            if (SteamAppId != null)
                return $"steam_{SteamAppId}.png";

            string store = Store.Replace(" ", "").ToLowerInvariant();

            if (StoreId != null)
            {
                string safeId = new string(StoreId
                    .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    .ToArray());
                if (!string.IsNullOrEmpty(safeId))
                    return $"{store}_{safeId}.png";
            }

            string safe = new string(Name
                .Where(c => char.IsLetterOrDigit(c) || c == '-')
                .ToArray());
            return string.IsNullOrEmpty(safe) ? null : $"{store}_{safe}.png";
        }
    }

    /// <summary>
    /// Persists game library sync state to %LOCALAPPDATA%\ArtLightControl\gamelibrarystate.json.
    /// Provides the APPSTORES JSON payload served by ArtLightBridge.
    /// </summary>
    public class GameLibraryState
    {
        public bool SyncEnabled { get; set; } = false;
        public string? SunshinePath { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public List<GameLibraryEntry> Games { get; set; } = new();

        /// <summary>
        /// Stable keys of games the user has explicitly removed via the Remove button.
        /// Auto-sync skips any discovered game whose key is in this set.
        /// Cleared when the user clicks "Sync Now" manually.
        /// </summary>
        public List<string> ExcludedGames { get; set; } = new();

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Returns the stable exclusion key for a game entry.</summary>
        public static string GetExclusionKey(GameLibraryEntry e) =>
            e.SteamAppId != null ? $"steam:{e.SteamAppId}" :
            e.StoreId    != null ? $"{e.Store}:{e.StoreId}" :
                                   $"{e.Store}:{e.Name}";


        /// <summary>
        /// Builds a duplicate-tolerant lookup from any keyed sequence — use this instead of
        /// <c>ToDictionary</c> for any key derived from user-editable library data.
        ///
        /// A persisted library can legitimately hold the same key twice. The clearest case is
        /// a display name: "Call of Duty®" ships from both Xbox and Steam, so the library
        /// holds two entries with that exact name and both are correct. <c>ToDictionary</c>
        /// throws ArgumentException ("An item with the same key has already been added") on
        /// that data, and because these lookups run on the UI thread the throw is stowed by
        /// XAML and kills the process with no log. Last entry wins — callers are looking a
        /// display name up to find a cover, and either copy is equivalent.
        /// </summary>
        public static Dictionary<TKey, TElement> MapBy<TKey, TElement>(
            IEnumerable<TElement>? source,
            Func<TElement, TKey> keySelector,
            IEqualityComparer<TKey>? comparer = null)
            where TKey : notnull
        {
            var map = new Dictionary<TKey, TElement>(comparer);
            if (source == null) return map;
            foreach (var item in source)
            {
                if (item is null) continue;
                map[keySelector(item)] = item;
            }
            return map;
        }

        /// <summary>
        /// Name → entry, case-insensitively, tolerating duplicate display names.
        /// See <see cref="MapBy"/> for why duplicate names are expected rather than corrupt.
        /// </summary>
        public static Dictionary<string, GameLibraryEntry> BuildNameMap(
            IEnumerable<GameLibraryEntry>? games) =>
            MapBy(games, g => g.Name, StringComparer.OrdinalIgnoreCase);


        // ── Static singleton ─────────────────────────────────────────────────

        private static GameLibraryState? _current;
        private static readonly object _currentLock = new();

        /// <summary>Singleton loaded from disk on first access. Thread-safe.</summary>
        public static GameLibraryState Current
        {
            get
            {
                lock (_currentLock)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        /// <summary>Forces a reload from disk on next access of Current.</summary>
        public static void Invalidate()
        {
            lock (_currentLock) { _current = null; }
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArtLightControl", "gamelibrarystate.json");

        public static GameLibraryState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new GameLibraryState();
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<GameLibraryState>(json) ?? new GameLibraryState();
            }
            catch
            {
                return new GameLibraryState();
            }
        }

        public void Save()
        {
            string tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                lock (_currentLock) { _current = this; } // keep singleton in sync
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // ── APPSTORES payload ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the JSON object {"Game Name": "Store", ...} for the APPSTORES command.
        /// </summary>
        public string ToAppStoresJson()
        {
            var enabled = Games.Where(g => g.Enabled).ToList();
            if (enabled.Count == 0) return "{}";

            // Duplicate display names are legal (same title from two stores), so build the
            // map by hand — ToDictionary would throw and take the whole command down.
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in enabled)
            {
                if (string.IsNullOrEmpty(g.Name)) continue;
                dict[g.Name] = g.Store;
            }
            return JsonSerializer.Serialize(dict);
        }
    }
}
