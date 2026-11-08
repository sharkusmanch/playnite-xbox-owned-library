using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XboxOwnedLibrary.Services;

namespace XboxOwnedLibrary
{
    public class SyncCandidate
    {
        public string ProductId { get; set; }
        public string PackageFamilyName { get; set; }
        public CatalogProduct Product { get; set; }
        public bool IsGold { get; set; }
        public bool IsPlayAnywhere => Product?.IsPlayAnywhere == true;
        public string Status { get; set; }
    }

    public class SyncResult
    {
        public int CollectionItems { get; set; }
        public int OwnedGames { get; set; }
        public int GoldGames { get; set; }
        public List<SyncCandidate> PcGames { get; } = new List<SyncCandidate>();
        public List<string> NotOnPc { get; } = new List<string>();
        public List<string> SkippedExtras { get; } = new List<string>();
        public List<SyncCandidate> Added { get; } = new List<SyncCandidate>();
        public List<Game> ImportedGames { get; } = new List<Game>();
        public int AlreadyInLibrary { get; set; }
        public int Excluded { get; set; }
        public int PlayAnywhereMarked { get; set; }
        public bool PlayAnywhereFeatureEnabled { get; set; }

        public string Summary(bool dryRun) =>
            $"{OwnedGames} owned games{(GoldGames > 0 ? $" + {GoldGames} Games with Gold" : "")} in the Xbox account, " +
            $"{PcGames.Count} playable on PC. " +
            $"{AlreadyInLibrary} already in Playnite, {Excluded} on the import exclusion list, " +
            $"{(dryRun ? "would add" : "added")} {Added.Count}." +
            (PlayAnywhereFeatureEnabled ? $" {(dryRun ? "Would mark" : "Marked")} {PlayAnywhereMarked} existing game(s) with the Play Anywhere feature." : "");
    }

    public class OwnedGamesSync
    {
        public static readonly Guid XboxLibraryId = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");
        public const string GoldTag = "Xbox Games with Gold";

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Regex _extraTitle = new Regex(
            @"\b(add-?on|upgrade|soundtrack|artbook|season pass|expansion pass|friend'?s pass|dlc|demo|beta|test server)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _nameNoise = new Regex(
            @"\s*(\((PC|Windows( 10)?)\)|[-–]\s*Windows( 10)?( Edition)?|for Windows( 10)?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IPlayniteAPI _api;
        private readonly XboxAuth _auth;
        private readonly OwnedLibraryClient _client;
        private readonly XboxOwnedLibrarySettings _settings;

        public OwnedGamesSync(IPlayniteAPI api, XboxAuth auth, HttpClient http, XboxOwnedLibrarySettings settings)
        {
            _api = api;
            _auth = auth;
            _settings = settings;
            _client = new OwnedLibraryClient(http);
        }

        public static LibraryPlugin FindXboxLibrary(IPlayniteAPI api)
        {
            return api.Addons.Plugins.FirstOrDefault(p => p.Id == XboxLibraryId) as LibraryPlugin;
        }

        public async Task<SyncResult> RunAsync(bool dryRun, string reportPath)
        {
            var xboxLibrary = FindXboxLibrary(_api);
            if (xboxLibrary == null)
            {
                throw new Exception("The Xbox library plugin is not installed or is disabled. This extension adds games through it.");
            }

            var tokens = await _auth.GetXstsTokensAsync(XboxAuth.XboxLiveRelyingParty, XboxAuth.MarketplaceRelyingParty).ConfigureAwait(false);
            var xuid = tokens[XboxAuth.XboxLiveRelyingParty].Claim("xid");
            if (string.IsNullOrEmpty(xuid))
            {
                throw new Exception("Xbox Live did not return an account ID (xuid).");
            }

            var result = new SyncResult();
            var items = await _client.GetCollectionAsync(tokens[XboxAuth.MarketplaceRelyingParty], xuid, _settings.Market).ConfigureAwait(false);
            result.CollectionItems = items.Count;

            var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // productId -> isGold
            foreach (var item in items)
            {
                var kind = OwnedLibraryClient.Classify(item);
                if (kind == OwnershipKind.Owned)
                {
                    result.OwnedGames++;
                    wanted[item.productId] = false;
                }
                else if (kind == OwnershipKind.Gold)
                {
                    result.GoldGames++;
                    if (_settings.ImportGamesWithGold && !wanted.ContainsKey(item.productId))
                    {
                        wanted[item.productId] = true;
                    }
                }
            }

            if (items.Count == 0)
            {
                throw new Exception("The Xbox account's Store collection came back empty. Refusing to treat that as an empty library.");
            }

            var catalog = await _client.GetCatalogAsync(wanted.Keys, _settings.Market, _settings.Language).ConfigureAwait(false);

            // Bundles carry no packages of their own; their parts do.
            var bundleParts = catalog.Values
                .Where(p => p.PcPackageFamilyName == null && !p.HasPackages)
                .SelectMany(p => p.BundledIds)
                .Where(id => !catalog.ContainsKey(id))
                .Distinct()
                .ToList();
            if (bundleParts.Count > 0)
            {
                var parts = await _client.GetCatalogAsync(bundleParts, _settings.Market, _settings.Language).ConfigureAwait(false);
                foreach (var part in parts)
                {
                    catalog[part.Key] = part.Value;
                }
            }

            var byPfn = new Dictionary<string, SyncCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in wanted)
            {
                if (!catalog.TryGetValue(entry.Key, out var product))
                {
                    result.NotOnPc.Add($"{entry.Key} (not in the {_settings.Market} catalog)");
                    continue;
                }

                if (product.IsDemo || _extraTitle.IsMatch(product.Title ?? ""))
                {
                    result.SkippedExtras.Add($"{product.Title} [{entry.Key}]");
                    continue;
                }

                var pcProducts = new List<CatalogProduct>();
                if (product.PcPackageFamilyName != null)
                {
                    pcProducts.Add(product);
                }
                else
                {
                    foreach (var partId in product.BundledIds)
                    {
                        if (catalog.TryGetValue(partId, out var part) &&
                            part.ProductKind == "Game" &&
                            part.PcPackageFamilyName != null &&
                            !part.IsDemo &&
                            !_extraTitle.IsMatch(part.Title ?? ""))
                        {
                            pcProducts.Add(part);
                        }
                    }
                }

                if (pcProducts.Count == 0)
                {
                    result.NotOnPc.Add($"{product.Title} [{entry.Key}]");
                    continue;
                }

                foreach (var pc in pcProducts)
                {
                    var pfn = pc.PcPackageFamilyName;
                    if (byPfn.TryGetValue(pfn, out var existing))
                    {
                        // An owned copy beats a Games with Gold grant of the same game.
                        existing.IsGold = existing.IsGold && entry.Value;
                        continue;
                    }

                    byPfn[pfn] = new SyncCandidate
                    {
                        ProductId = pc.ProductId,
                        PackageFamilyName = pfn,
                        Product = pc,
                        IsGold = entry.Value
                    };
                }
            }

            result.PcGames.AddRange(byPfn.Values.OrderBy(a => a.Product.Title));

            var inLibrary = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in _api.Database.Games.Where(g => g.PluginId == XboxLibraryId && g.GameId != null))
            {
                if (!inLibrary.ContainsKey(game.GameId))
                {
                    inLibrary[game.GameId] = game;
                }
            }

            var markPlayAnywhere = _settings.AddPlayAnywhereFeature && !string.IsNullOrWhiteSpace(_settings.PlayAnywhereFeatureName);
            result.PlayAnywhereFeatureEnabled = markPlayAnywhere;
            var toMark = new List<Game>();
            var excluded = new HashSet<string>(
                _api.Database.ImportExclusions.Where(e => e.LibraryId == XboxLibraryId && e.GameId != null).Select(e => e.GameId),
                StringComparer.OrdinalIgnoreCase);

            var toAdd = new List<SyncCandidate>();
            foreach (var candidate in result.PcGames)
            {
                if (inLibrary.TryGetValue(candidate.PackageFamilyName, out var existingGame))
                {
                    candidate.Status = "already in Playnite";
                    result.AlreadyInLibrary++;
                    if (markPlayAnywhere && candidate.IsPlayAnywhere && !HasFeature(existingGame, _settings.PlayAnywhereFeatureName))
                    {
                        candidate.Status = dryRun ? "in Playnite, would mark" : "in Playnite, marked";
                        toMark.Add(existingGame);
                    }
                }
                else if (excluded.Contains(candidate.PackageFamilyName))
                {
                    candidate.Status = "on import exclusion list";
                    result.Excluded++;
                }
                else
                {
                    candidate.Status = dryRun ? "would add" : "added";
                    toAdd.Add(candidate);
                }
            }

            if (!dryRun && toAdd.Count > 0)
            {
                using (_api.Database.BufferedUpdate())
                {
                    foreach (var candidate in toAdd)
                    {
                        try
                        {
                            var game = _api.Database.ImportGame(ToMetadata(candidate), xboxLibrary);
                            logger.Info($"Added owned Xbox game {game.Name} ({candidate.PackageFamilyName}).");
                            result.Added.Add(candidate);
                            result.ImportedGames.Add(game);
                        }
                        catch (Exception e)
                        {
                            candidate.Status = "failed: " + e.Message;
                            logger.Error(e, $"Failed to add {candidate.Product.Title} ({candidate.PackageFamilyName}).");
                        }
                    }
                }
            }
            else
            {
                result.Added.AddRange(toAdd);
            }

            result.PlayAnywhereMarked = toMark.Count;
            if (!dryRun && toMark.Count > 0)
            {
                var feature = _api.Database.Features.Add(_settings.PlayAnywhereFeatureName.Trim(), SameName);
                using (_api.Database.BufferedUpdate())
                {
                    foreach (var game in toMark)
                    {
                        try
                        {
                            if (game.FeatureIds == null)
                            {
                                game.FeatureIds = new List<Guid>();
                            }

                            game.FeatureIds.Add(feature.Id);
                            _api.Database.Games.Update(game);
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, $"Failed to add the Play Anywhere feature to {game.Name}.");
                        }
                    }
                }
            }

            WriteReport(result, dryRun, reportPath);
            return result;
        }

        private GameMetadata ToMetadata(SyncCandidate candidate)
        {
            var product = candidate.Product;
            var loc = product.Localized;
            var metadata = new GameMetadata
            {
                Name = CleanName(product.Title),
                GameId = candidate.PackageFamilyName,
                Source = new MetadataNameProperty("Xbox"),
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                IsInstalled = false,
                Links = new List<Link> { new Link("Microsoft Store", $"https://www.microsoft.com/store/productId/{candidate.ProductId}") }
            };

            if (candidate.IsGold)
            {
                metadata.Tags = new HashSet<MetadataProperty> { new MetadataNameProperty(GoldTag) };
            }

            if (candidate.IsPlayAnywhere && _settings.AddPlayAnywhereFeature && !string.IsNullOrWhiteSpace(_settings.PlayAnywhereFeatureName))
            {
                metadata.Features = new HashSet<MetadataProperty> { new MetadataNameProperty(_settings.PlayAnywhereFeatureName.Trim()) };
            }

            // By default leave details and artwork empty so Playnite's metadata download
            // fills them from the user's own sources.
            if (!_settings.UseStoreMetadata)
            {
                return metadata;
            }

            if (!string.IsNullOrEmpty(loc?.DeveloperName))
            {
                metadata.Developers = new HashSet<MetadataProperty> { new MetadataNameProperty(loc.DeveloperName) };
            }

            if (!string.IsNullOrEmpty(loc?.PublisherName))
            {
                metadata.Publishers = new HashSet<MetadataProperty> { new MetadataNameProperty(loc.PublisherName) };
            }

            var genres = product.Properties?.Categories?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (genres?.Count > 0)
            {
                metadata.Genres = new HashSet<MetadataProperty>(genres.Select(a => new MetadataNameProperty(a)));
            }

            var release = product.MarketProperties?.FirstOrDefault()?.OriginalReleaseDate;
            if (release != null && release.Value.Year > 1971)
            {
                metadata.ReleaseDate = new ReleaseDate(release.Value);
            }

            if (!string.IsNullOrEmpty(loc?.ShortDescription))
            {
                metadata.Description = loc.ShortDescription;
            }

            var cover = product.ImageUrl("Poster", "BoxArt", "BrandedKeyArt");
            var background = product.ImageUrl("SuperHeroArt", "TitledHeroArt", "Screenshot");
            var icon = product.ImageUrl("Logo", "BoxArt");
            if (cover != null) metadata.CoverImage = new MetadataFile(cover);
            if (background != null) metadata.BackgroundImage = new MetadataFile(background);
            if (icon != null) metadata.Icon = new MetadataFile(icon);

            return metadata;
        }

        private static bool SameName(DatabaseObject item, string name) =>
            string.Equals(item.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase);

        private bool HasFeature(Game game, string name) =>
            game.FeatureIds?.Any(id => _api.Database.Features[id] is GameFeature feature && SameName(feature, name)) == true;

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var clean = name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
            clean = _nameNoise.Replace(clean, "").Trim();
            return string.IsNullOrEmpty(clean) ? name.Trim() : clean;
        }

        private static void WriteReport(SyncResult result, bool dryRun, string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Xbox owned library sync ({(dryRun ? "preview, no changes" : "applied")}) {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine(result.Summary(dryRun));
                sb.AppendLine($"Collection items: {result.CollectionItems}");
                sb.AppendLine();
                sb.AppendLine("== PC games");
                foreach (var c in result.PcGames)
                {
                    sb.AppendLine($"{c.Status,-26} {CleanName(c.Product.Title)}{(c.IsGold ? " [Gold]" : "")}{(c.IsPlayAnywhere ? " [Play Anywhere]" : "")}  {c.ProductId}  {c.PackageFamilyName}");
                }

                sb.AppendLine();
                sb.AppendLine("== Owned but not playable on PC");
                result.NotOnPc.OrderBy(a => a).ForEach(a => sb.AppendLine(a));
                sb.AppendLine();
                sb.AppendLine("== Skipped as add-ons, demos or betas");
                result.SkippedExtras.OrderBy(a => a).ForEach(a => sb.AppendLine(a));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to write sync report.");
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
        {
            foreach (var item in source)
            {
                action(item);
            }
        }
    }
}
