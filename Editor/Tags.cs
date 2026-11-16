using System.IO;
using System.Globalization;
using System.Text;
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
    private static readonly Dictionary<string, string> Uses = new() {
        ["公路"]="#0397FC", ["拉力"]="#FC5A00", ["越野"]="#199049", ["直线加速"]="#E51F79", ["漂移"]="#000000"
    };
    private static readonly Dictionary<string, string> Styles = new() {
        ["操控"]="#F49AC2", ["均衡"]="#F2C34D", ["动力"]="#C41932"
    };
    private static readonly Dictionary<string, string> Strengths = new() {
        ["Overpower"]=Styles["动力"], ["META"]=Styles["均衡"], ["T1"]=Uses["直线加速"],
        ["T2"]=S1, ["T3"]=Uses["公路"], ["TOY"]=Uses["越野"]
    };
    internal static readonly Dictionary<string, string>[] DefaultColors = [Uses, Styles, Strengths];
    internal static string Foreground(string tag) => tag switch
    {
        "漂移" or "动力" or "Overpower" or "T2" => "#FFFFFF",
        "T1" => "#000000",
        _ => "#101010"
    };
    internal static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}

internal sealed record TagDefinition(string Id, string Name, string Color)
{
    internal string Foreground => TagCatalog.Foreground(this);
}

internal sealed record TagCategory(string Name, int MaxWidth, Func<TagSet, string?> Read);

internal sealed class TagCatalog
{
    internal const string Untagged = "未标记";
    internal static readonly TagCategory[] Categories = [
        new("赛事类型", 8, tags => tags.Use),
        new("性能倾向", 6, tags => tags.Style),
        new("强度", 10, tags => tags.Strength)
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly TagDefinition[][] groups;
    private readonly Dictionary<string, TagDefinition>[] byId;

    internal static readonly TagCatalog Default = new(TagColors.DefaultColors
        .Select(colors => colors.Select(pair => new TagDefinition(pair.Key, pair.Key, pair.Value)).ToArray())
        .ToArray());

    internal TagCatalog(TagDefinition[][] groups)
    {
        if (groups.Length != Categories.Length) throw new InvalidDataException("标签类别数量有误。");
        var normalized = new TagDefinition[groups.Length][];
        for (int category = 0; category < groups.Length; category++)
        {
            string categoryName = Categories[category].Name;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var group = groups[category] ?? throw new InvalidDataException($"「{categoryName}」缺少标签列表。");
            normalized[category] = group.Select(tag =>
            {
                if (tag?.Name is null) throw new InvalidDataException($"「{categoryName}」缺少标签名称。");
                string name = tag.Name.Trim().Normalize(NormalizationForm.FormC);
                if (string.IsNullOrWhiteSpace(tag.Id) || !ids.Add(tag.Id))
                    throw new InvalidDataException($"「{categoryName}」的标签编号无效。");
                if (!IsValidName(name, Categories[category].MaxWidth))
                    throw new InvalidDataException($"「{categoryName}」的标签名须包含可显示的字符，长度为 1–{Categories[category].MaxWidth} 个字符单位。");
                if (!names.Add(name) || name == Untagged)
                    throw new InvalidDataException($"「{categoryName}」中的标签名「{name}」重复或为保留名称。");
                if (!IsColor(tag.Color))
                    throw new InvalidDataException($"「{name}」的颜色须为 #RRGGBB，例如 #0397FC。");
                return tag with { Name = name, Color = tag.Color.ToUpperInvariant() };
            }).ToArray();
        }
        this.groups = normalized;
        byId = normalized.Select(group => group.ToDictionary(tag => tag.Id, StringComparer.Ordinal)).ToArray();
    }

    internal IReadOnlyList<TagDefinition> this[int category] => groups[category];
    internal TagDefinition? Find(int category, string? id) => id is null ? null : byId[category].GetValueOrDefault(id);

    internal bool Contains(TagSet tags) => KeepDefined(tags) == tags;

    internal TagSet KeepDefined(TagSet tags) => new(
        Find(0, tags.Use)?.Id, Find(1, tags.Style)?.Id, Find(2, tags.Strength)?.Id);

    internal bool Matches(TagSet tags, TagSet filters)
    {
        var assigned = KeepDefined(tags);
        return MatchesFilter(assigned.Use, filters.Use) &&
            MatchesFilter(assigned.Style, filters.Style) &&
            MatchesFilter(assigned.Strength, filters.Strength);
    }

    // null selects all cars, while an empty filter ID selects untagged cars.
    private static bool MatchesFilter(string? assigned, string? filter) =>
        filter is null || (assigned ?? "") == filter;

    internal static TagCatalog Load(string file) => File.Exists(file)
        ? new(JsonSerializer.Deserialize<TagDefinition[][]>(File.ReadAllBytes(file))
            ?? throw new InvalidDataException("标签配置为空。"))
        : Default;

    internal void Save(string file, TagCatalog previous)
    {
        using var mutex = new Mutex(false, @"Local\FH6GarageEditor.TagCatalog");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                throw new IOException("另一个窗口正在保存标签配置，请重试。");
        }
        catch (AbandonedMutexException) { /* The previous writer exited and this window owns the mutex. */ }
        try
        {
            var current = Load(file);
            if (!current.groups.Zip(previous.groups).All(pair => pair.First.SequenceEqual(pair.Second)))
                throw new InvalidOperationException("标签配置已在另一个窗口更新。请重新打开标签管理后再编辑。");
            TagStore.AtomicWrite(file, JsonSerializer.SerializeToUtf8Bytes(groups, JsonOptions));
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#' &&
        color.Skip(1).All(char.IsAsciiHexDigit);

    private static bool IsValidName(string name, int maxWidth)
    {
        if (name.Length == 0) return false;
        int width = 0;
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned)
                return false;
            width += rune.IsAscii ? 1 : 2;
        }
        return width <= maxWidth;
    }

    internal static string Foreground(TagDefinition tag)
    {
        // Keep the original text colors for the original badge backgrounds.
        if (TagColors.DefaultColors.Any(colors => colors.TryGetValue(tag.Id, out var color) && color == tag.Color))
            return TagColors.Foreground(tag.Id);
        int rgb = int.Parse(tag.Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static double Linear(int channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        double luminance = 0.2126 * Linear(rgb >> 16) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
        return luminance >= 0.179 ? "#000000" : "#FFFFFF";
    }
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

    internal void Save(Car car, TagSet tagsForCar, TagCatalog? catalog = null)
    {
        if (!(catalog ?? TagCatalog.Default).Contains(tagsForCar))
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
