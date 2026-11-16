using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FH6GarageEditor;

internal static class BadgePatch
{
    internal const string Resource = "Resources/Anthem/Shared_CarSelect.xaml";
    internal const string Override = "mediapc/UI/" + Resource;

    internal static byte[] Build(string original, IEnumerable<(Car Car, TagSet Tags)> records, TagCatalog? catalog = null)
    {
        catalog ??= TagCatalog.Default;
        using var ui = ZipFile.OpenRead(original);
        using var stream = ui.GetEntry(Resource)?.Open() ?? throw new InvalidDataException("未找到原生车卡模板。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = reader.ReadToEnd();
        var rows = records.Select(pair => (pair.Car, Tags: catalog.KeepDefined(pair.Tags)))
            .Where(pair => pair.Tags != TagSet.Empty).ToArray();
        if (rows.Any(row => row.Car.Id <= 0) || rows.Select(row => row.Car.Id).Distinct().Count() != rows.Length)
            throw new InvalidDataException("标签包含无效的单车编号。");
        var definitions = new StringBuilder();
        foreach (var (car, _) in rows)
            definitions.AppendLine($"    <System:Int32 x:Key=\"GarageEditorId{car.Id}\">{car.Id}</System:Int32>");
        var badges = new StringBuilder("\n                        <Viewbox StretchDirection=\"DownOnly\" MaxWidth=\"288\" HorizontalAlignment=\"Center\">\n                            <StackPanel Orientation=\"Horizontal\">\n");
        int index = 0;
        for (int category = 0; category < TagCatalog.Categories.Length; category++)
        {
            var read = TagCatalog.Categories[category].Read;
            var carsByTag = rows.ToLookup(row => read(row.Tags), row => row.Car.Id);
            foreach (var tag in catalog[category])
            {
                var ids = carsByTag[tag.Id];
                if (!ids.Any()) continue;
                var map = $"GarageEditorVisibility{index++}";
                definitions.AppendLine($"    <local:MappingConverter x:Key=\"{map}Map\" Default=\"{{x:Static Visibility.Collapsed}}\">");
                foreach (var id in ids)
                    definitions.AppendLine($"        <local:MappingConverterEntry Key=\"{{StaticResource GarageEditorId{id}}}\" Value=\"{{x:Static Visibility.Visible}}\" />");
                definitions.AppendLine("    </local:MappingConverter>");
                definitions.AppendLine($"    <local:MultiConverter x:Key=\"{map}\">");
                definitions.AppendLine("        <local:MultiConverterEntry Converter=\"{StaticResource A_Int64ToInt32Converter}\" TargetType=\"{x:Type System:Int32}\" />");
                definitions.AppendLine($"        <local:MultiConverterEntry Converter=\"{{StaticResource {map}Map}}\" />");
                definitions.AppendLine("    </local:MultiConverter>");
                var escaped = string.Concat(tag.Name.EnumerateRunes().Select(character => $"&#x{character.Value:X};"));
                badges.AppendLine($"                                <Border Background=\"{tag.Color}\" Padding=\"7,3,7,3\" Margin=\"0,6,4,0\" Visibility=\"{{Binding GarageId, Converter={{StaticResource {map}}}, FallbackValue=Collapsed}}\">");
                badges.AppendLine($"                                    <local:FZTextBlock Text=\"{escaped}\" Color=\"{tag.Foreground}\" CapHeight=\"12\" />");
                badges.AppendLine("                                </Border>");
            }
        }
        badges.Append("                            </StackPanel>\n                        </Viewbox>");
        const string anchor = "<local:LinearTransformConverter x:Key=\"A_Int64ToInt32Converter\" />";
        const string template = "<DataTemplate x:Key=\"A_DataTemplate_CarSelectCarouselItem\">";
        const string name = "x:Name=\"ManufacturerContainer\"";
        int resourceAt = source.IndexOf(anchor, StringComparison.Ordinal);
        int templateAt = source.IndexOf(template, StringComparison.Ordinal);
        int nameAt = templateAt < 0 ? -1 : source.IndexOf(name, templateAt, StringComparison.Ordinal);
        int badgeAt = nameAt < 0 ? -1 : source.IndexOf("</Decorator>", nameAt, StringComparison.Ordinal);
        if (resourceAt < 0 || templateAt < resourceAt || badgeAt < 0 ||
            !source.AsSpan(badgeAt + "</Decorator>".Length).TrimStart().StartsWith("</StackPanel>", StringComparison.Ordinal))
            throw new InvalidDataException("此游戏版本的车卡模板需要适配。");
        string patched = source.Insert(badgeAt + "</Decorator>".Length, badges.ToString())
            .Insert(resourceAt + anchor.Length, "\n" + definitions);
        var xml = XDocument.Parse(patched);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        if (!XNode.DeepEquals(xml.Descendants().Single(node => (string?)node.Attribute(xaml + "Name") == "star_icon"),
                             XDocument.Parse(source).Descendants().Single(node => (string?)node.Attribute(xaml + "Name") == "star_icon")))
            throw new InvalidDataException("收藏图标绑定校验失败。");
        return Encoding.UTF8.GetBytes(patched);
    }

    internal static void Apply(string game, IEnumerable<(Car Car, TagSet Tags)> records, TagCatalog catalog)
    {
        if (Process.GetProcessesByName("forzahorizon6").Length != 0)
            throw new InvalidOperationException("请退出 FH6 后应用徽标。标签已保存在编辑器里。");
        var target = Path.Combine(game, Override);
        var receipt = Path.Combine(GarageReader.Data, "badge-installed.sha256");
        if (File.Exists(target))
        {
            var current = File.ReadAllBytes(target);
            bool owned = File.Exists(receipt) && File.ReadAllText(receipt) == Hash(current);
            var previousZip = Path.Combine(AppContext.BaseDirectory, "..", "FH6-Per-Car-Badge-Preview.zip");
            if (!owned && File.Exists(previousZip))
            {
                using var zip = ZipFile.OpenRead(previousZip);
                using var entry = zip.GetEntry(Override)?.Open();
                if (entry != null)
                {
                    using var copy = new MemoryStream();
                    entry.CopyTo(copy);
                    owned = current.AsSpan().SequenceEqual(copy.ToArray());
                }
            }
            if (!owned) throw new InvalidOperationException("发现其他车卡覆盖文件，请先备份或移走它。");
        }
        var bytes = Build(Path.Combine(game, "media", "UI.zip"), records, catalog);
        TagStore.AtomicWrite(target, bytes);
        TagStore.AtomicWrite(receipt, Encoding.ASCII.GetBytes(Hash(bytes)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
