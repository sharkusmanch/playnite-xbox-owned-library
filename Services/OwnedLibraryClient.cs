using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace XboxOwnedLibrary.Services
{
    public class CollectionItem
    {
        public string productId { get; set; }
        public string productKind { get; set; }
        public string status { get; set; }
        public bool? isTrial { get; set; }
        public string skuType { get; set; }
        public bool? licensingRequired { get; set; }
        public DateTime? acquiredDate { get; set; }
    }

    public enum OwnershipKind
    {
        Owned,
        Gold,
        Ignored
    }

    /// <summary>
    /// Reads the account's Microsoft Store collection (every product it holds),
    /// then the public catalog for titles, platforms and package names.
    /// </summary>
    public class OwnedLibraryClient
    {
        private const string CollectionsUrl = "https://collections.mp.microsoft.com/v7.0/collections/query";
        private const string CatalogUrl = "https://displaycatalog.mp.microsoft.com/v7.0/products";
        // The collection holds every product kind (movies, DLC, consumables...);
        // a real account measured 3,258 items. The server ignores productTypes,
        // so filtering happens here.
        private const int PageSize = 1000;
        private const int MaxPages = 50;
        private const int CatalogBatch = 20;

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HttpClient _http;

        public OwnedLibraryClient(HttpClient http)
        {
            _http = http;
        }

        public static OwnershipKind Classify(CollectionItem item)
        {
            if (string.IsNullOrEmpty(item.productId) ||
                item.productId.StartsWith("CFQ7TTC0", StringComparison.OrdinalIgnoreCase) ||
                item.productKind != "Game" ||
                item.status != "Active" ||
                item.isTrial == true ||
                item.skuType == "Trial")
            {
                return OwnershipKind.Ignored;
            }

            return item.licensingRequired == true ? OwnershipKind.Gold : OwnershipKind.Owned;
        }

        private class CollectionsResponse
        {
            public List<CollectionItem> items { get; set; }
            public string continuationToken { get; set; }
        }

        public async Task<List<CollectionItem>> GetCollectionAsync(XboxAuth.XstsToken marketplaceToken, string xuid, string market)
        {
            var body = new Dictionary<string, object>
            {
                { "maxPageSize", PageSize },
                { "excludeDuplicates", true },
                { "market", market },
                { "validityType", "All" },
                { "beneficiaries", new[]
                    {
                        new Dictionary<string, string>
                        {
                            { "identityType", "xuid" },
                            { "identityValue", xuid },
                            { "localTicketReference", "playnite" }
                        }
                    }
                }
            };

            var items = new List<CollectionItem>();
            for (var page = 1; page <= MaxPages; page++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, CollectionsUrl))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", marketplaceToken.AuthorizationHeader);
                    req.Headers.Add("MS-CV", Guid.NewGuid().ToString("N").ToUpperInvariant() + ".0");
                    req.Content = new StringContent(Serialization.ToJson(body), Encoding.UTF8, "application/json");
                    var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception($"collections.mp.microsoft.com answered {(int)resp.StatusCode} on page {page}: {text}");
                    }

                    var data = Serialization.FromJson<CollectionsResponse>(text);
                    if (data?.items == null)
                    {
                        throw new Exception($"collections.mp.microsoft.com sent no item list on page {page}.");
                    }

                    items.AddRange(data.items);
                    if (string.IsNullOrEmpty(data.continuationToken) || data.items.Count == 0)
                    {
                        return items;
                    }

                    body["continuationToken"] = data.continuationToken;
                }
            }

            throw new Exception($"collections.mp.microsoft.com sent more than {MaxPages} pages; refusing to act on a partial list.");
        }

        public async Task<Dictionary<string, CatalogProduct>> GetCatalogAsync(IEnumerable<string> productIds, string market, string language)
        {
            var result = new Dictionary<string, CatalogProduct>(StringComparer.OrdinalIgnoreCase);
            var ids = productIds.Select(a => a.ToUpperInvariant()).Distinct().ToList();
            for (var i = 0; i < ids.Count; i += CatalogBatch)
            {
                var batch = ids.Skip(i).Take(CatalogBatch);
                var url = $"{CatalogUrl}?bigIds={string.Join(",", batch)}&market={market}&languages={language},neutral";
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new Exception($"displaycatalog answered {(int)resp.StatusCode}: {text}");
                }

                var data = Serialization.FromJson<CatalogResponse>(text);
                foreach (var product in data?.Products ?? new List<CatalogProduct>())
                {
                    if (!string.IsNullOrEmpty(product.ProductId))
                    {
                        result[product.ProductId] = product;
                    }
                }
            }

            return result;
        }
    }

    public class CatalogResponse
    {
        public List<CatalogProduct> Products { get; set; }
    }

    public class CatalogProduct
    {
        public string ProductId { get; set; }
        public string ProductKind { get; set; }
        public List<LocalizedProperty> LocalizedProperties { get; set; }
        public List<MarketProperty> MarketProperties { get; set; }
        public ProductProperties Properties { get; set; }
        public List<SkuAvailability> DisplaySkuAvailabilities { get; set; }

        public class LocalizedProperty
        {
            public string ProductTitle { get; set; }
            public string DeveloperName { get; set; }
            public string PublisherName { get; set; }
            public string ShortDescription { get; set; }
            public string ProductDescription { get; set; }
            public List<CatalogImage> Images { get; set; }
        }

        public class CatalogImage
        {
            public string ImagePurpose { get; set; }
            public string Uri { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public class MarketProperty
        {
            public DateTime? OriginalReleaseDate { get; set; }
        }

        public class ProductProperties
        {
            public string PackageFamilyName { get; set; }
            public string Category { get; set; }
            public List<string> Categories { get; set; }
            public bool? IsDemo { get; set; }
        }

        public class SkuAvailability
        {
            public Sku Sku { get; set; }
        }

        public class Sku
        {
            public SkuProperties Properties { get; set; }
        }

        public class SkuProperties
        {
            public bool? XboxXPA { get; set; }
            public List<Package> Packages { get; set; }
            public List<BundledSku> BundledSkus { get; set; }
        }

        public class Package
        {
            public string PackageFamilyName { get; set; }
            public List<PlatformDependency> PlatformDependencies { get; set; }
        }

        public class PlatformDependency
        {
            public string PlatformName { get; set; }
        }

        public class BundledSku
        {
            public string BigId { get; set; }
        }

        private static readonly HashSet<string> _pcPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows.Desktop",
            "Windows.Universal"
        };

        public LocalizedProperty Localized => LocalizedProperties?.FirstOrDefault();

        public string Title => Localized?.ProductTitle;

        private IEnumerable<SkuProperties> Skus =>
            DisplaySkuAvailabilities?.Select(a => a.Sku?.Properties).Where(a => a != null) ?? Enumerable.Empty<SkuProperties>();

        private IEnumerable<Package> Packages => Skus.SelectMany(a => a.Packages ?? new List<Package>());

        public bool HasPackages => Packages.Any();

        /// <summary>
        /// Xbox Play Anywhere: one purchase covers Xbox and PC. The per-SKU flag agreed with
        /// the product's XPA attribute on all 939 products checked (2026-11-03).
        /// </summary>
        public bool IsPlayAnywhere => Skus.Any(a => a.XboxXPA == true);

        /// <summary>Package family name of the PC package, or null when there is none.</summary>
        public string PcPackageFamilyName
        {
            get
            {
                var pc = Packages.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.PackageFamilyName) &&
                    p.PlatformDependencies?.Any(d => _pcPlatforms.Contains(d.PlatformName)) == true);
                if (pc != null)
                {
                    return pc.PackageFamilyName;
                }

                // Some Play Anywhere games (HITMAN 3, Boltgun) list only Xbox packages, which
                // carry no package name; the product-level one is the PC package.
                return IsPlayAnywhere && !string.IsNullOrEmpty(Properties?.PackageFamilyName)
                    ? Properties.PackageFamilyName
                    : null;
            }
        }

        public bool IsDemo => Properties?.IsDemo == true;

        public List<string> BundledIds =>
            Skus.SelectMany(a => a.BundledSkus ?? new List<BundledSku>())
                .Select(a => a.BigId)
                .Where(a => !string.IsNullOrEmpty(a))
                .Select(a => a.ToUpperInvariant())
                .Distinct()
                .ToList();

        public string ImageUrl(params string[] purposes)
        {
            var images = Localized?.Images;
            if (images == null)
            {
                return null;
            }

            foreach (var purpose in purposes)
            {
                var image = images.Where(a => string.Equals(a.ImagePurpose, purpose, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Width)
                    .FirstOrDefault();
                if (image?.Uri != null)
                {
                    return image.Uri.StartsWith("//") ? "https:" + image.Uri : image.Uri;
                }
            }

            return null;
        }
    }
}
