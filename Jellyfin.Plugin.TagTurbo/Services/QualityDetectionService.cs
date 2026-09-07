using System.Collections.Concurrent;
using System.IO;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using VideoRange = Jellyfin.Data.Enums.VideoRange;
using VideoRangeType = Jellyfin.Data.Enums.VideoRangeType;

namespace Jellyfin.Plugin.TagTurbo.Services;

/// <summary>
/// Service for detecting video quality from media items.
/// </summary>
public class QualityDetectionService : IQualityDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<QualityDetectionService> _logger;
    private readonly ConcurrentDictionary<Guid, (List<BadgeInfo> Badges, DateTime CachedAt)> _badgeCache = new();
    private static readonly TimeSpan BadgeCacheTtl = TimeSpan.FromMinutes(5);
    private DateTime _lastCacheCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(10);

    public QualityDetectionService(
        ILibraryManager libraryManager,
        ILogger<QualityDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public VideoQuality GetQuality(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogDebug("Item not found: {ItemId}", itemId);
            return VideoQuality.Unknown;
        }

        return GetQualityFromItem(item);
    }

    public static VideoQuality DetermineQuality(int width, int height)
    {
        var maxDimension = Math.Max(width, height);

        if (maxDimension >= 3800) return VideoQuality.UHD4K;
        if (maxDimension >= 1900) return VideoQuality.FHD1080p;
        if (maxDimension >= 1260) return VideoQuality.HD720p;
        if (maxDimension > 0) return VideoQuality.SD;
        return VideoQuality.Unknown;
    }

    /// <inheritdoc />
    public VideoQuality GetQualityFromItem(BaseItem item)
    {
        if (item is Video video)
        {
            return GetQualityFromVideo(video);
        }

        var (bestQuality, _) = GetBestChildVideo(item);

        if (bestQuality != VideoQuality.Unknown)
        {
            _logger.LogDebug("Resolved quality {Quality} for parent item: {ItemName}", bestQuality, item.Name);
        }

        return bestQuality;
    }

    /// <summary>
    /// Finds the best-quality child video (Movie/Episode) under a parent item, along with the
    /// video that produced it. Shared by <see cref="GetQualityFromItem"/> (resolution lookups)
    /// and <see cref="DetectAllBadgesInternal"/> (the overlay pipeline) so both stay consistent.
    /// </summary>
    private (VideoQuality BestQuality, Video? BestVideo) GetBestChildVideo(BaseItem item)
    {
        var query = new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode]
        };
        var children = _libraryManager.GetItemList(query);

        var bestQuality = VideoQuality.Unknown;
        Video? bestVideo = null;

        foreach (var child in children)
        {
            if (child is not Video childVideo) continue;

            var q = GetQualityFromVideo(childVideo);
            if (q != VideoQuality.Unknown && (bestQuality == VideoQuality.Unknown || q > bestQuality))
            {
                bestQuality = q;
                bestVideo = childVideo;
            }

            bestVideo ??= childVideo;
        }

        return (bestQuality, bestVideo);
    }

    /// <inheritdoc />
    public List<BadgeInfo> DetectAllBadges(BaseItem item)
    {
        if (_badgeCache.TryGetValue(item.Id, out var cached) && DateTime.UtcNow - cached.CachedAt < BadgeCacheTtl)
        {
            return new List<BadgeInfo>(cached.Badges);
        }

        var badges = DetectAllBadgesInternal(item);
        _badgeCache[item.Id] = (badges, DateTime.UtcNow);

        // Periodically evict expired entries to prevent unbounded memory growth
        if (DateTime.UtcNow - _lastCacheCleanup > CacheCleanupInterval)
        {
            _lastCacheCleanup = DateTime.UtcNow;
            var expiredKeys = _badgeCache
                .Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > BadgeCacheTtl)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _badgeCache.TryRemove(key, out _);
            }
        }
        return badges;
    }

    /// <inheritdoc />
    public void ClearBadgeCache()
    {
        _badgeCache.Clear();
    }

    private List<BadgeInfo> DetectAllBadgesInternal(BaseItem item)
    {
        var badges = new List<BadgeInfo>();

        if (item is Video video)
        {
            DetectBadgesFromVideo(video, badges);
        }
        else
        {
            var (bestResolution, bestVideo) = GetBestChildVideo(item);

            if (bestResolution != VideoQuality.Unknown)
            {
                badges.Add(CreateResolutionBadge(bestResolution));
            }

            if (bestVideo != null)
            {
                DetectHdrAndAudioBadges(bestVideo, badges);
            }

            // Series / Season / folder items: also check the item path itself
            // (disc markers often live on the series or season folder, not the episode)
            if (!badges.Any(b => b.Category == BadgeCategory.Source))
            {
                DetectDiscSource(item.Path, badges);
            }
        }

        return badges;
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: true);
    }

    private void DetectHdrAndAudioBadges(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: false);
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges, bool includeResolution)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            if (videoStream != null)
            {
                if (includeResolution)
                {
                    var width = videoStream.Width ?? 0;
                    var height = videoStream.Height ?? 0;
                    var quality = DetermineQuality(width, height);
                    if (quality != VideoQuality.Unknown)
                    {
                        badges.Add(CreateResolutionBadge(quality));
                    }
                }

                // HDR detection - always detect, filtering happens in ShouldShowBadge
                var hdrBadge = DetectHdr(videoStream);
                if (hdrBadge != null)
                {
                    badges.Add(hdrBadge);
                }

                // Video codec detection
                var codec = videoStream.Codec?.ToLowerInvariant() ?? string.Empty;
                if (codec is "h264" or "avc")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "h264", ResourceFileName = "badge-h264.svg" });
                }
                else if (codec is "hevc" or "h265")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "hevc", ResourceFileName = "badge-hevc.svg" });
                }
                else if (codec == "av1")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "av1", ResourceFileName = "badge-av1.svg" });
                }
                else if (codec == "vp9")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "vp9", ResourceFileName = "badge-vp9.svg" });
                }
            }

            // 3D detection
            if (video.Video3DFormat.HasValue)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.ThreeD,
                    BadgeKey = "3d",
                    ResourceFileName = "badge-3d.svg"
                });
            }

            // Disc source detection (DVD / Blu-ray) — walk up from episode path for series support
            DetectDiscSource(video.Path, badges);

            // STRM detection (file extension only, same approach as .disc markers) — own panel so it
            // shows regardless of whether the underlying stream has been probed for a real codec
            DetectStrmSource(video.Path, badges);

            // Audio detection - prefer the default audio track
            var allAudioStreams = mediaSource?.MediaStreams?.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (allAudioStreams != null && allAudioStreams.Count > 0)
            {
                var defaultStream = allAudioStreams.FirstOrDefault(s => s.IsDefault);
                var streamsToAnalyze = defaultStream != null
                    ? new List<MediaStream> { defaultStream }
                    : new List<MediaStream> { allAudioStreams[0] };
                var audioBadges = DetectAudio(streamsToAnalyze);
                badges.AddRange(audioBadges);
            }

            // Language detection - always detect all, filtering by mode happens in ShouldShowBadge
            var allStreams = mediaSource?.MediaStreams;
            if (allStreams != null)
            {
                var langBadges = DetectLanguages(allStreams.ToList());
                badges.AddRange(langBadges);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect badges for video: {ItemName}", video.Name);
        }
    }

    private static readonly Dictionary<string, string> LangCodeToFlag = new(StringComparer.OrdinalIgnoreCase)
    {
        { "fre", "fra" }, { "ger", "deu" }, { "dut", "nld" }, { "cze", "ces" }, { "rum", "ron" }, { "chi", "zho" },
        { "gre", "ell" }, { "may", "msa" }, { "tgl", "fil" }, { "slo", "slk" }, { "baq", "eus" }, { "wel", "cym" }
    };

    // Only include language codes that have a matching flag-{code}.svg asset
    private static readonly HashSet<string> KnownFlagCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fra", "eng", "jpn", "deu", "spa", "ita", "por", "kor", "zho", "rus",
        "nld", "ara", "hin", "tha", "pol", "tur", "swe", "dan", "nor", "fin",
        "ces", "hun", "ron", "ukr", "vie", "heb"
    };

    private static string GetFlagResourceFileName(string langCode)
    {
        var normalized = LangCodeToFlag.TryGetValue(langCode, out var mapped) ? mapped : langCode;
        return KnownFlagCodes.Contains(normalized) ? $"flag-{normalized.ToLowerInvariant()}.svg" : string.Empty;
    }

    /// <summary>
    /// Detects all language and subtitle badges. Always detects all languages;
    /// filtering by mode (DefaultOnly/All) is done in ShouldShowBadge.
    /// </summary>
    private static List<BadgeInfo> DetectLanguages(List<MediaStream> allStreams)
    {
        var badges = new List<BadgeInfo>();
        var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audioStreams.Count == 0) return badges;

        var addedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect all audio languages
        foreach (var stream in audioStreams)
        {
            var lang = stream.Language;
            if (!string.IsNullOrEmpty(lang) && addedLanguages.Add(lang))
            {
                var langLower = lang.ToLowerInvariant();
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Language,
                    BadgeKey = langLower,
                    ResourceFileName = GetFlagResourceFileName(langLower)
                });
            }
        }

        // VOST indicators - always detect, filtering happens in ShouldShowBadge
        var audioLanguages = new HashSet<string>(
            audioStreams.Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => s.Language!.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var subtitleStreams = allStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        foreach (var sub in subtitleStreams)
        {
            var subLang = sub.Language?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(subLang) && !audioLanguages.Contains(subLang))
            {
                var key = "vost" + subLang;
                if (addedLanguages.Add(key))
                {
                    badges.Add(new BadgeInfo
                    {
                        Category = BadgeCategory.Subtitle,
                        BadgeKey = key,
                        ResourceFileName = string.Empty
                    });
                }
            }
        }

        return badges;
    }

    private static BadgeInfo? DetectHdr(MediaStream videoStream)
    {
        var rangeType = videoStream.VideoRangeType;

        // Dolby Vision variants (highest priority)
        if (rangeType is VideoRangeType.DOVI
            or VideoRangeType.DOVIWithHDR10
            or VideoRangeType.DOVIWithHLG
            or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "dv", ResourceFileName = "badge-dv.svg" };
        }

        if (rangeType == VideoRangeType.HDR10Plus)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10plus", ResourceFileName = "badge-hdr10plus.svg" };
        }

        if (rangeType == VideoRangeType.HLG)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hlg", ResourceFileName = "badge-hlg.svg" };
        }

        if (rangeType == VideoRangeType.HDR10)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10", ResourceFileName = "badge-hdr10.svg" };
        }

        if (videoStream.VideoRange == VideoRange.HDR)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr", ResourceFileName = "badge-hdr.svg" };
        }

        return null;
    }

    private static List<BadgeInfo> DetectAudio(IEnumerable<MediaStream> audioStreams)
    {
        var badges = new List<BadgeInfo>();
        BadgeInfo? codecBadge = null;
        int codecPriority = -1;
        int bestChannels = 0;

        foreach (var stream in audioStreams)
        {
            var codec = stream.Codec?.ToUpperInvariant() ?? string.Empty;
            var profile = stream.Profile?.ToUpperInvariant() ?? string.Empty;
            var channels = stream.Channels ?? 0;

            if (channels > bestChannels) bestChannels = channels;

            int priority = -1;
            BadgeInfo? candidate = null;

            if (profile.Contains("ATMOS"))
            {
                priority = 7;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "atmos", ResourceFileName = "badge-atmos.svg" };
            }
            else if (codec == "TRUEHD")
            {
                priority = 6;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "truehd", ResourceFileName = "badge-truehd.svg" };
            }
            else if (profile.Contains("DTS:X") || profile.Contains("DTS-X") || profile.Contains("DTSX"))
            {
                priority = 5;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtsx", ResourceFileName = "badge-dtsx.svg" };
            }
            else if (profile.Contains("DTS-HD MA") || profile.Contains("DTS-HD MASTER") || (codec == "DTS" && profile.Contains("MA")))
            {
                priority = 4;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtshdma", ResourceFileName = "badge-dtshdma.svg" };
            }

            if (candidate != null && priority > codecPriority)
            {
                codecPriority = priority;
                codecBadge = candidate;
            }
        }

        if (codecBadge != null) badges.Add(codecBadge);

        if (bestChannels >= 8)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "7.1", ResourceFileName = "badge-7_1.svg" });
        else if (bestChannels >= 6)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" });
        else if (bestChannels >= 2)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "stereo", ResourceFileName = "badge-stereo.svg" });

        return badges;
    }


    /// <summary>
    /// Detects DVD / Blu-ray source by walking up from the media/item path.
    /// Checks each directory (episode → season → series) for:
    /// - [dvd] / [blu-ray] / [bluray] in the folder name
    /// - [dvd] / [blu-ray] / [bluray] in any .disc / .disc.bak filename
    /// - presence of a plain .disc / .disc.bak marker (defaults to Blu-ray)
    /// Does NOT inspect media file names (.mkv, .mp4, etc.).
    /// </summary>
    private static void DetectDiscSource(string? itemPath, List<BadgeInfo> badges)
    {
        if (string.IsNullOrEmpty(itemPath))
            return;

        try
        {
            // Start at containing folder if path is a file, otherwise use path as directory
            string? directory;
            if (File.Exists(itemPath))
                directory = Path.GetDirectoryName(itemPath);
            else if (Directory.Exists(itemPath))
                directory = itemPath;
            else
                directory = Path.GetDirectoryName(itemPath);

            if (string.IsNullOrEmpty(directory))
                return;

            bool isDvd = false;
            bool isBluray = false;
            bool hasDiscMarker = false;

            // Walk up: episode folder → season → series (cap to avoid library root noise)
            const int maxLevels = 5;
            for (var level = 0; level < maxLevels && !string.IsNullOrEmpty(directory); level++)
            {
                if (!Directory.Exists(directory))
                    break;

                var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
                var folderLower = folderName.ToLowerInvariant();

                if (folderLower.Contains("[dvd]"))
                    isDvd = true;
                if (folderLower.Contains("[blu-ray]") || folderLower.Contains("[bluray]"))
                    isBluray = true;

                try
                {
                    foreach (var discPath in Directory.EnumerateFiles(directory))
                    {
                        var name = Path.GetFileName(discPath);
                        if (string.IsNullOrEmpty(name))
                            continue;

                        var nameLower = name.ToLowerInvariant();
                        var isDiscFile =
                            nameLower is ".disc" or ".disc.bak" ||
                            nameLower.EndsWith(".disc", StringComparison.Ordinal) ||
                            nameLower.EndsWith(".disc.bak", StringComparison.Ordinal);

                        if (!isDiscFile)
                            continue;

                        hasDiscMarker = true;

                        if (nameLower.Contains("[dvd]"))
                            isDvd = true;
                        if (nameLower.Contains("[blu-ray]") || nameLower.Contains("[bluray]"))
                            isBluray = true;
                    }
                }
                catch
                {
                    // Skip unreadable directories
                }

                // Stop early once we have an explicit tag
                if (isDvd || isBluray)
                    break;

                var parent = Directory.GetParent(directory);
                directory = parent?.FullName;
            }

            if (isDvd)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Source,
                    BadgeKey = "dvd",
                    ResourceFileName = "badge-dvd.svg"
                });
            }
            else if (isBluray)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Source,
                    BadgeKey = "bluray",
                    ResourceFileName = "badge-bluray.svg"
                });
            }
            else if (hasDiscMarker)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Source,
                    BadgeKey = "bluray",
                    ResourceFileName = "badge-bluray.svg"
                });
            }
        }
        catch
        {
            // Ignore path/permission errors
        }
    }

    /// <summary>
    /// Detects STRM source by checking the media file extension.
    /// </summary>
    private static void DetectStrmSource(string? itemPath, List<BadgeInfo> badges)
    {
        if (string.IsNullOrEmpty(itemPath))
            return;

        if (string.Equals(Path.GetExtension(itemPath), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add(new BadgeInfo
            {
                Category = BadgeCategory.FileType,
                BadgeKey = "strm",
                ResourceFileName = "badge-strm.svg"
            });
        }
    }

    private static BadgeInfo CreateResolutionBadge(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.UHD4K => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" },
            VideoQuality.FHD1080p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "1080p", ResourceFileName = "badge-1080p.svg" },
            VideoQuality.HD720p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "720p", ResourceFileName = "badge-720p.svg" },
            VideoQuality.SD => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "sd", ResourceFileName = "badge-sd.svg" },
            _ => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "unknown", ResourceFileName = string.Empty }
        };
    }

    private VideoQuality GetQualityFromVideo(Video video)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream == null) return VideoQuality.Unknown;

            var width = videoStream.Width ?? 0;
            var height = videoStream.Height ?? 0;
            return DetermineQuality(width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get media sources for video item: {ItemName}", video.Name);
            return VideoQuality.Unknown;
        }
    }
}
