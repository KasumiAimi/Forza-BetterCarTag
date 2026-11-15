using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace FH6GarageEditor;

internal sealed record TagSet(string? Use, string? Style, string? Strength = null)
{
    internal static readonly TagSet Empty = new(null, null);
}

internal static class TagColors
{
    internal const string S1 = "#A84AD6";
    internal static readonly Dictionary<string, string> Uses = new() {
        ["公路"]="#0397FC", ["拉力"]="#FC5A00", ["越野"]="#199049", ["直线加速"]="#E51F79", ["漂移"]="#000000"
    };
    internal static readonly Dictionary<string, string> Styles = new() {
        ["操控"]="#F49AC2", ["均衡"]="#F2C34D", ["动力"]="#C41932"
    };
    internal static readonly Dictionary<string, string> Strengths = new() {
        ["Overpower"]=Styles["动力"], ["META"]=Styles["均衡"], ["T1"]=Uses["直线加速"],
        ["T2"]=S1, ["T3"]=Uses["公路"], ["TOY"]=Uses["越野"]
    };
    internal static readonly (Dictionary<string, string> Colors, Func<TagSet, string?> Read)[] Categories = [
        (Uses, tags => tags.Use),
        (Styles, tags => tags.Style),
        (Strengths, tags => tags.Strength)
    ];
    internal static string Foreground(string tag) => tag switch
    {
        "漂移" or "动力" or "Overpower" or "T2" => "#FFFFFF",
        "T1" => "#000000",
        _ => "#101010"
    };
    internal static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}

internal sealed class TagStore
{
    private readonly string file;
    private readonly Dictionary<string, TagSet> tags;

    internal TagStore(string folder, string profileKey)
    {
        file = Path.Combine(folder, profileKey + ".json");
        tags = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, TagSet>>(File.ReadAllBytes(file)) ?? []
            : [];
    }

    private static string Key(Car car) => $"{car.Id}:{car.ModelId}:{car.Vin}";
    internal TagSet Get(Car car) => tags.GetValueOrDefault(Key(car), TagSet.Empty);

    internal void Save(Car car, TagSet tagsForCar)
    {
        if (TagColors.Categories.Any(category => category.Read(tagsForCar) is string label && !category.Colors.ContainsKey(label)))
            throw new ArgumentException("标签不受支持。");
        var next = new Dictionary<string, TagSet>(tags);
        if (tagsForCar == TagSet.Empty) next.Remove(Key(car));
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
