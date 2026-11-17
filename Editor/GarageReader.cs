using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FH6CarEditor;
using FH6LocalCryptoTool;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace FH6GarageEditor;

internal sealed record SaveSource(string Path, string ProfileKey, bool Backup, DateTime Modified);
internal sealed record Car(int Id, int ModelId, string Vin, bool Favorite, int Pi, int ClassId,
    string Thumbnail, string Make, string Name, int Year, int Rarity, string? Tire, int? Horsepower,
    int? WeightKg, string? DriveLayout);

internal static class GarageReader
{
    internal static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaHorizon6");
    internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FH6GarageEditor");

    internal static string? FindGame()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value.Replace(@"\\", @"\")));
        return libraries.Select(library => Path.Combine(library, "steamapps", "common", "ForzaHorizon6"))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "forzahorizon6.exe")));
    }

    internal static List<SaveSource> FindSaves()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Local))
            foreach (var metadata in Directory.EnumerateDirectories(Local, "SaveFolderMetadata_*"))
            {
                var file = Path.Combine(metadata, "LastSaveFolderLocation");
                if (!File.Exists(file) || new FileInfo(file).Length > 4096) continue;
                string candidate;
                try { candidate = File.ReadAllText(file).Trim('\0', '\r', '\n', '\uFEFF'); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (Path.IsPathFullyQualified(candidate) && Directory.Exists(candidate)) roots.Add(candidate);
            }
        var pgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "GameSaves", "pgs");
        var systemPgs = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "XboxGames", "GameSave", "pgs");
        var live = roots.SelectMany(FindLiveSaves)
            .Concat(new[] { pgs, systemPgs }.SelectMany(FindPgsSaves))
            .GroupBy(source => source.ProfileKey).Select(group => group.MaxBy(source => source.Modified)!)
            .OrderByDescending(source => source.Modified).ToList();
        if (live.Count != 0) return live;
        var backup = Path.Combine(Local, "LocalStorage_Shared");
        return Directory.Exists(backup)
            ? FindFiles(backup, "*ProfileBackup").Take(80).Select(file => Source(file, Path.GetDirectoryName(file)!, true))
                .OrderByDescending(source => source.Modified).ToList()
            : [];
    }

    private static IEnumerable<SaveSource> FindPgsSaves(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var profile in Directory.EnumerateDirectories(folder, "u_*_16D460", options))
        {
            var current = FindLiveSaves(Path.Combine(profile, "current", "ContainersRoot")).ToArray();
            if (current.Length == 0)
            {
                // Gaming Services can leave only the numbered generation on disk.
                var generations = Directory.EnumerateDirectories(profile, "*", options)
                    .Select(path => (Path: path, Version: uint.TryParse(Path.GetFileName(path), out uint version) ? (uint?)version : null))
                    .Where(item => item.Version.HasValue)
                    .OrderByDescending(item => item.Version);
                current = generations.Select(item => FindLiveSaves(Path.Combine(item.Path, "ContainersRoot")).ToArray())
                    .FirstOrDefault(saves => saves.Length > 0) ?? [];
            }
            foreach (var save in current) yield return save;
        }
    }

    private static IEnumerable<SaveSource> FindLiveSaves(string root) => Directory.Exists(root)
        ? FindFiles(root, "C_ProfileData").Select(file => Source(file, root, false))
        : [];

    private static IEnumerable<string> FindFiles(string root, string pattern) => Directory.EnumerateFiles(root, pattern,
        new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true });

    internal static SaveSource Source(string file, string scope, bool backup)
    {
        var profile = new DirectoryInfo(scope);
        var account = profile;
        while (account.Parent is not null && !account.Name.StartsWith("u_", StringComparison.OrdinalIgnoreCase))
            account = account.Parent;
        var identity = account.Name.StartsWith("u_", StringComparison.OrdinalIgnoreCase) ? account.Name : profile.FullName;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..24];
        return new SaveSource(file, key, backup, File.GetLastWriteTime(file));
    }

    internal static List<Car> Read(SaveSource source, string? game)
    {
        Directory.CreateDirectory(Data);
        var catalog = new Dictionary<int, (string Media, string Make, string Name, int Year, int Rarity)>();
        var performanceBands = new Dictionary<int, PerformanceBand>();
        var tires = new Dictionary<(int Model, int Part), string>();
        var driveLayouts = new Dictionary<(int Model, int Part), string>();
        if (game is not null)
        {
            var file = Path.Combine(game, "media", "stripped", "gamedbRC.slt");
            if (File.Exists(file))
            {
                var bytes = GameDb.Decrypt(ReadShared(file, 96 * 1024 * 1024), Fh6Keys.Get("GameDB").DataKey);
                Query(bytes, "SELECT Id, MediaName, Year, BaseRarity FROM Data_Car", reader =>
                {
                    int id = reader.GetInt32(0);
                    string media = reader.GetString(1);
                    string make = CarDisplayNames.Manufacturer(id, media, media.Split('_')[0]);
                    string name = CarDisplayNames.Name(media) ?? media.Replace('_', ' ');
                    if (name.StartsWith(make + " ", StringComparison.OrdinalIgnoreCase)) name = name[(make.Length + 1)..];
                    catalog[id] = (media, make, name, (int)CarDisplayNames.Year(id, media, reader.GetInt32(2)), reader.GetInt32(3));
                });
                Query(bytes, "SELECT Id, MaxPerformanceIndex, MaxDisplayPerformanceIndex FROM CarClasses ORDER BY Id", reader =>
                    performanceBands[reader.GetInt32(0)] = new PerformanceBand(reader.GetDouble(1), reader.GetInt32(2)));
                Query(bytes, "SELECT Ordinal, Id, TireModelName, IsStock FROM List_UpgradeTireCompound", reader =>
                {
                    if (!reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)))
                        tires[(reader.GetInt32(0), reader.GetInt32(1))] =
                            VehicleDetails.TireName(reader.GetString(2), reader.GetInt32(3) == 1);
                });
                Query(bytes, """
                    SELECT upgrade.Ordinal, upgrade.Id, drive.DrivetypeID
                    FROM List_UpgradeDrivetrain AS upgrade
                    JOIN Data_Drivetrain AS drive ON drive.DrivetrainID = upgrade.DrivetrainID
                    """, reader =>
                {
                    if (!reader.IsDBNull(2) && VehicleDetails.DriveLayout(reader.GetInt32(2)) is string layout)
                        driveLayouts[(reader.GetInt32(0), reader.GetInt32(1))] = layout;
                });
            }
        }
        byte[] save = ReadShared(source.Path, 96 * 1024 * 1024);
        var cars = new List<Car>();
        Query(ProfileDatabase(save), """
            SELECT Id, CarId, Guid, IsFavorite, PerformanceIndex, ClassID, Thumbnail,
                   TireCompound, SimPeakPower, CurbWeight, Drivetrain
            FROM Career_Garage ORDER BY Id
            """, reader =>
        {
            int id = reader.GetInt32(0);
            int model = reader.GetInt32(1);
            var definition = catalog.GetValueOrDefault(model, (Media: "", Make: "其他", Name: $"车型 {model}", Year: 0, Rarity: 0));
            double? pi = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            int classId = reader.IsDBNull(5) ? -1 : reader.GetInt32(5);
            string? tire = reader.IsDBNull(7) ? null : tires.GetValueOrDefault((model, reader.GetInt32(7)));
            int? horsepower = reader.IsDBNull(8) ? null : VehicleDetails.Horsepower(reader.GetDouble(8));
            int? weight = reader.IsDBNull(9) ? null : VehicleDetails.WeightKg(reader.GetDouble(9));
            string? layout = reader.IsDBNull(10) ? null : driveLayouts.GetValueOrDefault((model, reader.GetInt32(10)));
            cars.Add(new Car(id, model, reader.GetValue(2).ToString() ?? "", !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                PerformanceRating.Display(pi, classId, performanceBands), classId,
                reader.IsDBNull(6) ? "" : reader.GetString(6), definition.Make, definition.Name, definition.Year, definition.Rarity,
                tire, horsepower, weight, layout));
        });
        if (cars.Select(car => car.Id).Distinct().Count() != cars.Count)
            throw new InvalidDataException("车库中存在无法区分的车辆编号。");
        return cars;
    }

    private static byte[] ReadShared(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is < 1 || stream.Length > limit) throw new InvalidDataException("资源文件大小超出支持范围。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length) throw new IOException("文件正在更新。");
        return bytes;
    }

    private static byte[] ProfileDatabase(byte[] save)
    {
        var key = Fh6Keys.Get("ProfileData");
        if (!ForzaZip.ValidateContainer(save, key.DataKey, key.MacKey, out _))
            throw new InvalidDataException("存档校验失败。请等游戏保存完毕，再刷新。");
        var decoded = ForzaZip.DecryptContainer(save, key.DataKey);
        int compressed = BinaryPrimitives.ReadInt32LittleEndian(decoded);
        int expected = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(4));
        if (compressed <= 0 || compressed > decoded.Length - 8 || expected is <= 0 or > 96 * 1024 * 1024)
            throw new InvalidDataException("存档格式不受支持。");
        using var stream = new ZLibStream(new MemoryStream(decoded, 8, compressed, false), CompressionMode.Decompress);
        var payload = new byte[expected];
        stream.ReadExactly(payload);
        if (stream.ReadByte() != -1) throw new InvalidDataException("存档长度不匹配。");
        int offset = 0;
        foreach (uint marker in new uint[] { 0x4A8BF2B6, 0xB60261FC, 0x986CA5CB, 0xA9F1F729 })
        {
            if (payload.Length - offset < 8 || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset)) != marker)
                throw new InvalidDataException("车库存档结构不受支持。");
            int size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 4));
            offset += 8;
            if (size < 0 || size > payload.Length - offset) throw new InvalidDataException("存档区段损坏。");
            if (marker == 0xA9F1F729) return payload.AsSpan(offset, size).ToArray();
            offset += size;
        }
        throw new InvalidDataException("存档中缺少车库数据库。");
    }

    private static void Query(byte[] bytes, string sql, Action<SqliteDataReader> read)
    {
        if (!bytes.AsSpan().StartsWith("SQLite format 3\0"u8)) throw new InvalidDataException("游戏数据无法识别。");
        var temporary = Path.Combine(Data, "read-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) read(reader);
        }
        finally { File.Delete(temporary); }
    }
}
