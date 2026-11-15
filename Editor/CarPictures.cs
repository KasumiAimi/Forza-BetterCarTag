using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FH6CarEditor;
using FH6LocalCryptoTool.GarageViewer;

namespace FH6GarageEditor;

internal sealed class CarPictures : IDisposable
{
    private readonly GarageThumbnailCache? cache;
    private readonly string? stockZip;
    private readonly Dictionary<int, string> stock = [];
    private readonly Dictionary<int, (string Zip, string Name)> stockTextures = [];
    private readonly Dictionary<int, Task<(BitmapSource? Image, string Source)>> stockCache = [];
    private readonly SemaphoreSlim decodeGate = new(3);

    internal CarPictures(string? game)
    {
        try
        {
            if (File.Exists(Path.Combine(GarageThumbnailCache.DefaultDirectory, ".manifest")))
                cache = new GarageThumbnailCache(GarageThumbnailCache.DefaultDirectory);
        }
        catch (IOException) { }
        if (game is null) return;
        var path = Path.Combine(game, "media", "UI", "Textures", "OfflineThumbnails.zip");
        try
        {
            if (File.Exists(path))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    int separator = entry.Name.IndexOf('_');
                    if (separator > 0 && entry.Length is > 0 and <= 8_388_608 &&
                        entry.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(entry.Name.AsSpan(0, separator), out var model))
                        stock.TryAdd(model, entry.FullName);
                }
                stockZip = path;
            }
        }
        catch (IOException) { }
        var resource = Path.Combine(game, "media", "stripped");
        if (!Directory.Exists(resource)) return;
        foreach (var archive in Directory.EnumerateFiles(resource, "RC*.zip")
                     .Where(file => Regex.IsMatch(Path.GetFileName(file), @"^RC\d+\.zip$", RegexOptions.IgnoreCase)))
        {
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var item in zip.Entries)
                {
                    var match = Regex.Match(item.Name, @"^(?:thumbnail_)?(\d+)(?:_(?:big|small))?\.swatchbin$", RegexOptions.IgnoreCase);
                    if (match.Success && item.Length is > 0 and <= 8_388_608 && int.TryParse(match.Groups[1].Value, out int id))
                        stockTextures.TryAdd(id, (archive, item.FullName));
                }
            }
            catch (IOException) { }
        }
    }

    internal async Task<(BitmapSource? Image, string Source)> Get(Car car)
    {
        if (cache is not null)
        {
            var exact = await cache.GetAsync(car.Thumbnail).ConfigureAwait(false);
            if (exact.Image is not null) return (exact.Image, "车库缩略图");
        }
        Task<(BitmapSource? Image, string Source)> task;
        lock (stockCache)
        {
            if (!stockCache.TryGetValue(car.ModelId, out task!))
            {
                if (stockCache.Count >= 96) stockCache.Clear();
                stockCache[car.ModelId] = task = LoadStock(car.ModelId);
            }
        }
        return await task.ConfigureAwait(false);
    }

    private async Task<(BitmapSource? Image, string Source)> LoadStock(int carId)
    {
        await decodeGate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => DecodeStock(carId)).ConfigureAwait(false); }
        finally { decodeGate.Release(); }
    }

    private (BitmapSource? Image, string Source) DecodeStock(int carId)
    {
        try
        {
            if (stockZip is not null && stock.TryGetValue(carId, out var key))
            {
                using var zip = ZipFile.OpenRead(stockZip);
                using var stream = zip.GetEntry(key)!.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                // Detach from the WIC WebP frame so WPF composites its alpha onto the card.
                if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096 ||
                    (long)frame.PixelWidth * frame.PixelHeight > 8_000_000)
                    return (null, "暂无车辆图片");
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                int stride = checked(converted.PixelWidth * 4);
                var pixels = new byte[checked(stride * converted.PixelHeight)];
                converted.CopyPixels(pixels, stride, 0);
                var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, stride);
                image.Freeze();
                return (image, "车型缩略图");
            }
            if (stockTextures.TryGetValue(carId, out var texture))
            {
                using var zip = ZipFile.OpenRead(texture.Zip);
                var entry = zip.GetEntry(texture.Name)!;
                using var stream = entry.Open();
                var bytes = new byte[(int)entry.Length];
                stream.ReadExactly(bytes);
                var image = SwatchThumbnailDecoder.Decode(bytes);
                if (image.PixelWidth > 420)
                {
                    double scale = 420d / image.PixelWidth;
                    image = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
                    image.Freeze();
                }
                return (image, "车型缩略图");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
        }
        return (null, "暂无车辆图片");
    }

    public void Dispose() => cache?.Dispose();
}
