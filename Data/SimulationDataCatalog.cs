using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TowerOfArgentum.Simulation;

namespace TowerOfArgentum.Api.Data;

/// <summary>Snapshot inmutable de una temporada de simulación.</summary>
public sealed class SimulationDataCatalog
{
    private static readonly string[] Files =
    [
        "enemigos.json", "clases.json", "perks.json", "planos.json",
        "generador-armas.json", "curvas.json", "combate.json", "sinergias.json",
    ];

    private readonly Dictionary<string, BalanceData> _byHash = new(StringComparer.Ordinal);

    public BalanceData Current { get; }

    public SimulationDataCatalog(string root, int currentVersion)
    {
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Falta la carpeta de simulación {root}.");

        // Cada subcarpeta numérica es un snapshot histórico. Nunca se reemplaza
        // el contenido de una temporada que ya tiene corridas.
        foreach (string directory in Directory.GetDirectories(root))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int version))
                continue;

            var data = Load(directory);
            if (data.Version != version)
                throw new InvalidOperationException($"La carpeta {directory} contiene datos v{data.Version}.");
            _byHash.Add(data.Hash, data);
        }

        Current = _byHash.Values.SingleOrDefault(x => x.Version == currentVersion)
            ?? throw new InvalidOperationException($"Faltan los datos de simulación v{currentVersion} en {root}.");
    }

    public BalanceData? Find(string hash, int version)
        => _byHash.TryGetValue(hash, out var data) && data.Version == version ? data : null;

    private static BalanceData Load(string directory)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = new StringBuilder();

        foreach (string name in Files)
        {
            string path = Path.Combine(directory, name);
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length == 0)
                throw new InvalidOperationException($"Archivo vacío: {path}");
            texts.Add(name, text);
            raw.Append(text);
        }

        var maps = new List<Map>();
        string layoutDirectory = Path.Combine(directory, "layouts");
        if (!Directory.Exists(layoutDirectory))
            throw new InvalidOperationException($"Falta {layoutDirectory}.");

        foreach (string path in Directory.GetFiles(layoutDirectory, "*.json")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length == 0)
                continue;

            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                continue;

            var map = AsciiLayout.Compose(rows.EnumerateArray().Select(x => x.GetString() ?? "").ToList());
            if (map is null)
                continue;

            maps.Add(map);
            raw.Append(text);
        }

        if (maps.Count == 0)
            throw new InvalidOperationException($"No hay layouts válidos en {layoutDirectory}.");

        var enemyText = texts["enemigos.json"];
        using var enemyDoc = JsonDocument.Parse(enemyText);
        int version = enemyDoc.RootElement.GetProperty("version").GetInt32();
        var enemies = JsonSerializer.Deserialize<EnemyFile>(enemyText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Enemies
            ?? throw new InvalidOperationException("enemigos.json no trae enemigos.");
        if (enemies.Count == 0)
            throw new InvalidOperationException("enemigos.json no trae enemigos.");

        var perks = TableReader.Perks(texts["perks.json"]);
        var data = new BalanceData
        {
            Version = version,
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                raw.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)))),
            Enemies = enemies.Select(x => new EnemyData
            {
                Id = x.Id, Name = x.Name ?? "?", Area = x.Area, Hp = x.ScaledHp,
                MinDamage = x.MinDamage, MaxDamage = x.MaxDamage, Speed = x.Speed,
                CadenceTicks = x.CadenceTicks, Gold = x.Gold, Body = x.Body,
                VisualScale = x.VisualScale, Graphic = x.Graphic ?? "",
            }).ToList(),
            Layouts = maps,
            Classes = TableReader.Classes(texts["clases.json"]),
            Perks = perks,
            Blueprints = TableReader.Blueprints(texts["planos.json"], perks),
            Weapons = TableReader.Weapons(texts["generador-armas.json"]),
            Curves = TableReader.Curves(texts["curvas.json"]),
            Combat = TableReader.Combat(texts["combate.json"]),
            Synergies = TableReader.Synergies(texts["sinergias.json"]),
        };
        data.BuildIndexes();
        return data;
    }

    private sealed class EnemyFile
    {
        public List<EnemyJson>? Enemies { get; set; }
    }

    private sealed class EnemyJson
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Area { get; set; }
        public int ScaledHp { get; set; }
        public int MinDamage { get; set; }
        public int MaxDamage { get; set; }
        public int Speed { get; set; }
        public int CadenceTicks { get; set; }
        public int Gold { get; set; }
        public int Body { get; set; }
        public int VisualScale { get; set; } = 100;
        public string? Graphic { get; set; }
    }
}
