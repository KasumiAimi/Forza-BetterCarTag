using System.IO;
using System.Security.Cryptography;
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
        var screenshotRatings = new (int Id, int ModelId, int ClassId, int Pi)[] {
            (54, 2017, 0, 400), (1300, 2740, 1, 450), (180, 2563, 1, 500),
            (1369, 2489, 2, 540), (1103, 3549, 2, 571), (207, 2489, 2, 600),
            (1521, 2574, 3, 663), (132, 1155, 3, 700), (1337, 1155, 3, 700),
            (1217, 296, 4, 800), (1405, 3655, 4, 800), (168, 2569, 5, 900)
        };
        var matchedRatings = new List<string>();
        foreach (var expected in screenshotRatings)
        {
            var car = cars.FirstOrDefault(car => car.Id == expected.Id && car.ModelId == expected.ModelId);
            if (car is null) continue;
            if (car.Pi != expected.Pi || car.ClassId != expected.ClassId)
                throw new Exception($"Saved car #{car.Id} PI {car.Pi} does not match the in-game screenshot ({expected.Pi}).");
            matchedRatings.Add($"#{car.Id}: {car.Pi}");
        }
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
        var tags = new TagStore(folder, source.ProfileKey);
        tags.Save(pair[0], new TagPair("公路", "操控"));
        tags.Save(pair[1], new TagPair("漂移", "动力"));
        var reopened = new TagStore(folder, source.ProfileKey);
        if (reopened.Get(pair[0]) != new TagPair("公路", "操控") || reopened.Get(pair[1]) != new TagPair("漂移", "动力"))
            throw new Exception("Duplicate model copies did not round trip independently.");
        var patch = BadgePatch.Build(Path.Combine(game, "media", "UI.zip"), cars.Select(car => (car, reopened.Get(car))));
        var labels = XDocument.Parse(System.Text.Encoding.UTF8.GetString(patch)).Descendants().Select(node => (string?)node.Attribute("Text")).ToHashSet();
        if (!new[] { "公路", "操控", "漂移", "动力" }.All(labels.Contains))
            throw new Exception("The badge override lost its labels.");
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
            "Cross-checked in-game ratings: " + string.Join(", ", matchedRatings),
            "R favorites: " + string.Join(", ", cars.Where(car => car.ClassId == 6 && car.Favorite).Select(car => $"#{car.Id}: {car.Pi}")),
            $"Favorite filter: {favorites.Length} cars. Favorite, class, manufacturer and text intersection verified.",
            "Classes: " + string.Join(", ", cars.GroupBy(car => car.ClassId).OrderBy(group => group.Key).Select(group => $"{group.Key}: {group.Count()}")),
            "Images: " + string.Join(", ", imageSources.Select(item => $"{item.Key}: {item.Value}")),
            "Independent tags, rebuilt badge override and unchanged save hash verified."
        }.Concat(probes));
    }
}
