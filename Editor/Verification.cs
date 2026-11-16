using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FH6GarageEditor;

internal static class Verification
{
    internal static void Run(string report)
    {
        string game = GarageReader.FindGame() ?? throw new Exception("Steam installation not detected.");
        var sources = GarageReader.FindSaves();
        var source = sources.FirstOrDefault() ?? throw new Exception("No local profile was detected.");
        var before = SHA256.HashData(File.ReadAllBytes(source.Path));
        var cars = GarageReader.Read(source, game);
        if (cars.Count < 2 || cars.All(car => car.Name.StartsWith("车型 ")))
            throw new Exception("Profile did not produce a named garage.");
        var bands = new Dictionary<int, PerformanceBand>
        {
            [0] = new(12, 400),
            [1] = new(22, 500)
        };
        if (PerformanceRating.Display(600, 2, bands) != 600 ||
            PerformanceRating.Display(17.05, 1, bands) != 451 ||
            PerformanceRating.Display(12, 1, bands) != 401 ||
            PerformanceRating.Display(23, 1, bands) != 500)
            throw new Exception("The displayed PI did not match the fixed test ratings.");
        var favorites = MainWindow.FilterCars(cars, null, "", null, true).ToArray();
        if (!favorites.SequenceEqual(cars.Where(car => car.Favorite)))
            throw new Exception("Favorite filter does not match the saved favorite flags.");
        var sampleFilter = (favorites.Length == 0 ? cars[0] : favorites[0]);
        var combined = MainWindow.FilterCars(cars, sampleFilter.Make, sampleFilter.Name, sampleFilter.ClassId, true);
        if (!combined.Any() && favorites.Length > 0 ||
            combined.Any(car => !car.Favorite || car.ClassId != sampleFilter.ClassId || car.Make != sampleFilter.Make))
            throw new Exception("Class, search and favorite filters do not combine correctly.");
        var pair = cars.GroupBy(car => (car.ModelId, car.Pi)).First(group => group.Count() >= 2).Take(2).ToArray();
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "verify-" + Guid.NewGuid().ToString("N"));
        var legacy = new Dictionary<string, object>
        {
            [$"{pair[0].Id}:{pair[0].ModelId}:{pair[0].Vin}"] = new { Use = "公路", Style = "操控" }
        };
        TagStore.AtomicWrite(Path.Combine(folder, source.ProfileKey + ".json"), JsonSerializer.SerializeToUtf8Bytes(legacy));
        var tags = new TagStore(folder, source.ProfileKey);
        if (tags.Get(pair[0]) != new TagSet("公路", "操控"))
            throw new Exception("The original two-label format did not load.");
        var expectedLabels = new Dictionary<Car, TagSet>
        {
            [pair[0]] = tags.Get(pair[0]) with { Strength = "Overpower" },
            [pair[1]] = new TagSet("漂移", "动力", "META"),
        };
        var otherCars = cars.Where(car => car.Id != pair[0].Id && car.Id != pair[1].Id).Take(4).ToArray();
        string[] otherStrengths = ["T1", "T2", "T3", "TOY"];
        foreach (var (car, label) in otherCars.Zip(otherStrengths))
            expectedLabels.Add(car, new TagSet(null, null, label));
        foreach (var (car, carTags) in expectedLabels)
            tags.Save(car, carTags);
        var reopened = new TagStore(folder, source.ProfileKey);
        if (expectedLabels.Any(entry => reopened.Get(entry.Key) != entry.Value))
            throw new Exception("The tags did not round trip independently.");
        var patch = BadgePatch.Build(Path.Combine(game, "media", "UI.zip"), cars.Select(car => (car, reopened.Get(car))));
        var elements = XDocument.Parse(System.Text.Encoding.UTF8.GetString(patch)).Descendants().ToArray();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (car, carTags) in expectedLabels)
        {
            for (int category = 0; category < TagCatalog.Categories.Length; category++)
            {
                if (TagCatalog.Categories[category].Read(carTags) is not string label) continue;
                var tag = TagCatalog.Default.Find(category, label)!;
                var badge = elements.Single(element => element.Name.LocalName == "FZTextBlock" &&
                    (string?)element.Attribute("Text") == label &&
                    ((string?)element.Parent?.Attribute("Visibility"))?.Contains("GarageEditorVisibility") == true);
                var binding = (string?)badge.Parent?.Attribute("Visibility") ?? "";
                var mapping = elements.Single(element => (string?)element.Attribute(xaml + "Key") is string key &&
                    key.EndsWith("Map") && binding.Contains($"{{StaticResource {key[..^3]}}}"));
                if ((string?)badge.Parent?.Attribute("Background") != tag.Color ||
                    (string?)mapping.Elements().Single().Attribute("Key") != $"{{StaticResource GarageEditorId{car.Id}}}")
                    throw new Exception($"The badge override changed the color or garage ID for {label}.");
            }
        }
        reopened.Save(pair[0], reopened.Get(pair[0]) with { Strength = null });
        reopened.Save(otherCars[0], TagSet.Empty);
        var cleared = new TagStore(folder, source.ProfileKey);
        if (cleared.Get(pair[0]) != new TagSet("公路", "操控") || cleared.Get(otherCars[0]) != TagSet.Empty ||
            cleared.Get(pair[1]) != expectedLabels[pair[1]])
            throw new Exception("Clearing a strength tag affected another tag or vehicle.");
        VerifyCustomTags(folder, Path.Combine(game, "media", "UI.zip"), pair);
        using var pictures = new CarPictures(game);
        var imageSources = new Dictionary<string, int>();
        var probes = new List<string>();
        foreach (var sample in cars)
        {
            var picture = pictures.Get(sample).GetAwaiter().GetResult();
            imageSources[picture.Source] = imageSources.GetValueOrDefault(picture.Source) + 1;
            if (sample.Id is 633 or 1378 or 136 or 760 && picture.Image is { } image)
            {
                var pixel = new byte[4];
                new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)
                    .CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                var visual = new DrawingVisual();
                var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                using (DrawingContext drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.White, null, rect);
                    drawing.DrawImage(image, rect);
                }
                var rendered = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);
                var background = new byte[4];
                rendered.CopyPixels(new Int32Rect(0, 0, 1, 1), background, 4, 0);
                if (pixel[3] == 0 && background.Any(channel => channel == 0))
                    throw new Exception($"Transparent thumbnail #{sample.Id} rendered on a black background.");
                probes.Add($"#{sample.Id} ({sample.ModelId}) {picture.Source}: {image.Format}, corner BGRA {Convert.ToHexString(pixel)}, rendered {Convert.ToHexString(background)}");
            }
        }
        if (imageSources.GetValueOrDefault("车库缩略图") == 0 && imageSources.GetValueOrDefault("车型缩略图") == 0)
            throw new Exception("No vehicle thumbnails could be decoded.");
        if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(source.Path))))
            throw new Exception("The saved profile changed during the read-only test.");
        File.WriteAllLines(report, new[] {
            $"Profiles: {sources.Count}, imported: {cars.Count}, named: {cars.Count(car => !car.Name.StartsWith("车型 "))}",
            "PI conversion checked with fixed test ratings.",
            "R favorites: " + string.Join(", ", cars.Where(car => car.ClassId == 6 && car.Favorite).Select(car => $"#{car.Id}: {car.Pi}")),
            $"Favorite filter: {favorites.Length} cars. Favorite, class, manufacturer and text intersection verified.",
            "Classes: " + string.Join(", ", cars.GroupBy(car => car.ClassId).OrderBy(group => group.Key).Select(group => $"{group.Key}: {group.Count()}")),
            "Images: " + string.Join(", ", imageSources.Select(item => $"{item.Key}: {item.Value}")),
            "Legacy tags, six strength badges by garage ID, independent clearing and unchanged save hash verified.",
            "Custom tag limits, catalog persistence, rename, recolor, deletion and native badge XML verified."
        }.Concat(probes));
    }

    private static void VerifyCustomTags(string folder, string original, Car[] cars)
    {
        string file = Path.Combine(folder, "tag-catalog.json");
        var originalCatalog = TagCatalog.Load(file);
        var groups = TagCatalog.Categories.Select((_, index) => originalCatalog[index].ToArray()).ToArray();
        var existing = groups[0][0];
        const string renamed = "路&𠀀";
        groups[0][0] = existing with { Name = renamed, Color = "#102030" };
        var deleted = groups[1][0];
        groups[1] = groups[1].Skip(1).ToArray();
        const string customId = "verification-custom";
        groups[2] = [.. groups[2], new TagDefinition(customId, "耐久MAX", "#F0E010")];
        var catalog = new TagCatalog(groups);
        catalog.Save(file, originalCatalog);
        catalog = TagCatalog.Load(file);
        var first = new TagStore(folder, "custom-first-profile");
        var second = new TagStore(folder, "custom-second-profile");
        first.Save(cars[0], new TagSet(existing.Id, deleted.Id), originalCatalog);
        second.Save(cars[1], new TagSet(existing.Id, null, customId), catalog);
        if (catalog.Find(0, first.Get(cars[0]).Use)?.Name != renamed ||
            catalog.KeepDefined(first.Get(cars[0])) != new TagSet(existing.Id, null) ||
            catalog.Find(0, second.Get(cars[1]).Use)?.Color != "#102030")
            throw new Exception("Renaming or deleting a tag changed the wrong car assignments.");
        var combinedFilters = new TagSet(existing.Id, "", customId);
        if (!catalog.Matches(second.Get(cars[1]), combinedFilters) ||
            catalog.Matches(first.Get(cars[0]), combinedFilters) ||
            !catalog.Matches(first.Get(cars[0]), new TagSet(existing.Id, "", "")) ||
            catalog.Matches(first.Get(cars[0]), new TagSet(existing.Id, deleted.Id)))
            throw new Exception("Combined tag filters did not distinguish matching, untagged, or deleted labels.");
        var elements = XDocument.Parse(System.Text.Encoding.UTF8.GetString(BadgePatch.Build(original,
            [(cars[0], first.Get(cars[0])), (cars[1], second.Get(cars[1]))], catalog))).Descendants().ToArray();
        var badge = elements.Single(element => element.Name.LocalName == "FZTextBlock" &&
            (string?)element.Attribute("Text") == renamed);
        if ((string?)badge.Parent?.Attribute("Background") != "#102030" ||
            (string?)badge.Attribute("Color") != "#FFFFFF" ||
            elements.Any(element => (string?)element.Attribute("Text") == deleted.Name) ||
            !elements.Any(element => (string?)element.Attribute("Text") == "耐久MAX"))
            throw new Exception("Custom labels or colors did not reach the generated native badges.");
        for (int index = 0; index < groups.Length; index++)
        {
            int limit = TagCatalog.Categories[index].MaxWidth;
            var invalid = groups.Select(group => group.ToArray()).ToArray();
            invalid[index][0] = invalid[index][0] with { Name = new string('长', limit / 2) + "A" };
            try { _ = new TagCatalog(invalid); }
            catch (InvalidDataException) { continue; }
            throw new Exception("An overlength tag name was accepted.");
        }
    }
}
