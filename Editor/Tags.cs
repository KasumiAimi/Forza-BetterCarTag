using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace FH6GarageEditor;

internal sealed record TagPair(string? Use, string? Style)
{
    internal static readonly TagPair Empty = new(null, null);
}

internal static class TagColors
{
    internal static readonly Dictionary<string, string> Uses = new() {
        ["公路"]="#0397FC", ["拉力"]="#FC5A00", ["越野"]="#199049", ["直线加速"]="#E51F79", ["漂移"]="#000000"
    };
    internal static readonly Dictionary<string, string> Styles = new() {
        ["操控"]="#F49AC2", ["均衡"]="#F2C34D", ["动力"]="#C41932"
    };
    internal static string Foreground(string tag) => tag is "漂移" or "动力" ? "#FFFFFF" : "#101010";
    internal static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}

internal sealed class TagStore
{
    private readonly string file;
    private readonly Dictionary<string, TagPair> tags;

    internal TagStore(string folder, string profileKey)
    {
        file = Path.Combine(folder, profileKey + ".json");
        tags = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, TagPair>>(File.ReadAllBytes(file)) ?? []
            : [];
    }

    private static string Key(Car car) => $"{car.Id}:{car.ModelId}:{car.Vin}";
    internal TagPair Get(Car car) => tags.GetValueOrDefault(Key(car), TagPair.Empty);

    internal void Save(Car car, TagPair tagsForCar)
    {
        if ((tagsForCar.Use is not null && !TagColors.Uses.ContainsKey(tagsForCar.Use)) ||
            (tagsForCar.Style is not null && !TagColors.Styles.ContainsKey(tagsForCar.Style)))
            throw new ArgumentException("标签不受支持。");
        var next = new Dictionary<string, TagPair>(tags);
        if (tagsForCar == TagPair.Empty) next.Remove(Key(car));
        else next[Key(car)] = tagsForCar;
        AtomicWrite(file, JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true }));
        tags.Clear();
        foreach (var pair in next) tags.Add(pair.Key, pair.Value);
    }

    internal static void AtomicWrite(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
