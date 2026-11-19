using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// 지역(맵 모듈) 하나의 전투용 지형. 좌표(모듈 로컬 칸)만 들고 있어 씬을 닫은 뒤에도 쓸 수 있다.
public class BalanceRegion
{
    public int Region;           // WaveSpawner.region - 웨이브·포탈 표의 지역 번호
    public int ModuleId;
    public int UnlockOrder;      // MapRegistry._sceneModules 순서(0 = 처음부터 열린 지역)
    public float CellSize;       // 칸 하나의 월드 크기. MoveSpeed(월드/초)를 칸/초로 바꿀 때 쓴다
    public int MinPortals;
    public int MaxPortals;
    public float BossSpawnDelay;
    public readonly List<List<Vector2Int>> GroundLanes = new();             // 스폰 -> 본진 칸 목록(스폰마다 하나)
    public readonly List<List<Vector2Int>> SwimLanes = new();               // 같은 스폰의 헤엄 경로
    public readonly Dictionary<Vector2Int, List<List<Vector2Int>>> AirRoutes = new(); // 스폰 칸 -> 저작한 공중 경로들
    public readonly List<Vector2Int> MeleeTiles = new();                    // 근접 영웅을 놓을 수 있는 칸
    public readonly List<Vector2Int> RangedTiles = new();                   // 원거리 영웅을 놓을 수 있는 칸
}

// 전투 계산에 쓰는 값 묶음. 지형·본진 체력은 MainScene에서, 웨이브·적·포탈은 CSV 표에서 읽는다.
public class BalanceBattleAssets
{
    public readonly List<BalanceRegion> Regions = new(); // UnlockOrder 순서
    public readonly WaveTable Waves = new();
    public readonly EnemyTable Enemies = new();
    public readonly PortalTable Portals = new();
    // 스킬·버프·장판 등 시뮬에 없는 영웅 능력을 실제 플레이 결과에 맞춰 메우는 보정(%). 창에서 입력한다.
    public int HeroDamagePercent = 100;
    public int HeroHpPercent = 100;
    public int BaseHp;                                              // GameManager 프리팹의 hp(타이틀 강화 전)
    public readonly List<BaseUpgradeData> BaseHpUpgrades = new();   // GameManager.hpUpgrades - 타이틀 강화로 늘어나는 본진 체력

    // 무엇을 못 읽었는지(다 읽었으면 null). 경고 문구에 그대로 띄운다.
    public string Problem =>
        Regions.Count == 0 ? "맵 모듈을 못 읽음(MainScene의 MapRegistry._sceneModules)" :
        Regions[0].GroundLanes.Count == 0 ? $"첫 지역(모듈 {Regions[0].ModuleId})의 지상 레인이 0개(스폰·본진 타일 또는 RouteConfig 확인)" :
        BaseHp <= 0 ? "본진 체력을 못 읽음(GameLifeTimeScope의 GameManagerPrefab)" :
        null;

    public bool IsValid => Problem == null;
}

// MainScene(BalanceSceneReader가 열어 준 씬)과 데이터 표에서 전투 값을 읽는다. 맵 코드는 읽기만 한다.
public static class BalanceBattleSource
{
    // 런타임 필드 이름. 인스펙터 전용(private) 값이라 SerializedObject로만 읽을 수 있다.
    private const string SceneModulesField = "_sceneModules";
    private const string MinPortalsField = "minActivePortals";
    private const string MaxPortalsField = "maxActivePortals";
    private const string BossDelayField = "bossSpawnDelay";
    private const string BaseHpField = "hp";
    private const string BaseHpUpgradesField = "hpUpgrades";
    private const string GameManagerPrefabField = "GameManagerPrefab";

    // 씬과 상관없는 데이터 표만 읽어 둔 빈 묶음. 씬 값은 ReadScene이 채운다.
    public static BalanceBattleAssets CreateWithTables()
    {
        var assets = new BalanceBattleAssets();
        assets.Waves.Load(DataTableIds.Wave);
        assets.Enemies.Load(DataTableIds.Enemy);
        assets.Portals.Load(DataTableIds.Portal);
        return assets;
    }

    public static void ReadScene(Scene scene, BalanceBattleAssets assets)
    {
        MapRegistry registry = null;
        GameLifeTimeScope scope = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (registry == null) registry = root.GetComponentInChildren<MapRegistry>(true);
            if (scope == null) scope = root.GetComponentInChildren<GameLifeTimeScope>(true);
        }

        if (scope != null) ReadBaseHp(scope, assets);
        if (registry != null) ReadModules(registry, assets);
    }

    // GameManager는 씬에 미리 있지 않고 GameLifeTimeScope가 시작할 때 프리팹으로 만든다 - 그 프리팹 값을 읽는다.
    private static void ReadBaseHp(GameLifeTimeScope scope, BalanceBattleAssets assets)
    {
        if (new SerializedObject(scope).FindProperty(GameManagerPrefabField).objectReferenceValue is not GameManager prefab) return;
        var serialized = new SerializedObject(prefab);
        assets.BaseHp = serialized.FindProperty(BaseHpField).intValue;
        SerializedProperty upgrades = serialized.FindProperty(BaseHpUpgradesField);
        for (int index = 0; index < upgrades.arraySize; index++)
        {
            if (upgrades.GetArrayElementAtIndex(index).objectReferenceValue is BaseUpgradeData upgrade) assets.BaseHpUpgrades.Add(upgrade);
        }
    }

    // MapRegistry.UnlockNextModule이 _sceneModules 순서대로 연다 - 이 순서가 곧 해금 순서다.
    private static void ReadModules(MapRegistry registry, BalanceBattleAssets assets)
    {
        SerializedProperty modules = new SerializedObject(registry).FindProperty(SceneModulesField);
        for (int index = 0; index < modules.arraySize; index++)
        {
            if (modules.GetArrayElementAtIndex(index).objectReferenceValue is not ModuleLogic module) continue;
            BalanceRegion region = ReadRegion(module, assets.Regions.Count);
            if (region != null) assets.Regions.Add(region);
        }
    }

    private static BalanceRegion ReadRegion(ModuleLogic module, int unlockOrder)
    {
        Grid grid = module.GetComponentInChildren<Grid>(true);
        WaveSpawner spawner = module.GetComponentInChildren<WaveSpawner>(true);
        if (grid == null || spawner == null) return null;

        var spawnerData = new SerializedObject(spawner);
        var region = new BalanceRegion
        {
            Region = spawner.Region,
            ModuleId = module.ModuleId,
            UnlockOrder = unlockOrder,
            CellSize = grid.cellSize.x,
            MinPortals = spawnerData.FindProperty(MinPortalsField).intValue,
            MaxPortals = spawnerData.FindProperty(MaxPortalsField).intValue,
            BossSpawnDelay = BossSpawnDelay(spawner),
        };

        // 경로 창(EnemyRouteWindow)과 같은 순서: 타일 수집 -> 좌표 격자 -> 런타임과 같은 LaneBuilder로 레인 계산.
        Dictionary<Vector2Int, Tile> cells = ModuleScan.MapCells(ModuleScan.CollectTiles(grid), out _, out _);
        foreach (LaneData lane in LaneQuery.BuildLanes(cells, module.GetComponentInChildren<RouteConfig>(true)))
        {
            if (!lane.IsValid) continue;
            region.GroundLanes.Add(ToCoords(lane.Tiles));
            region.SwimLanes.Add(ToCoords(lane.SwimTiles));
        }
        ReadPlacement(cells, region);
        ReadAirRoutes(cells, spawner.EnemyRoutes, region);
        return region;
    }

    // WaveSpawner.bossSpawnDelay는 [SerializeField]가 없어 인스펙터 값으로는 못 읽는다 - 컴포넌트 필드를 그대로 읽는다.
    private static float BossSpawnDelay(WaveSpawner spawner) =>
        (float)typeof(WaveSpawner).GetField(BossDelayField, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(spawner);

    private static void ReadPlacement(Dictionary<Vector2Int, Tile> cells, BalanceRegion region)
    {
        foreach (Tile tile in cells.Values)
        {
            if (tile.State.CanMelee) region.MeleeTiles.Add(tile.Coord);
            if (tile.State.CanRanged) region.RangedTiles.Add(tile.Coord);
        }
    }

    // 공중 적은 저작 경로(EnemyRouteSet)를 따른다. 런타임과 같은 해석기(EnemyRoutePath.BuildTiles)로 칸 목록을 만든다.
    private static void ReadAirRoutes(Dictionary<Vector2Int, Tile> cells, EnemyRouteSet routes, BalanceRegion region)
    {
        if (routes == null) return;
        foreach (EnemyRouteSet.Entry entry in routes.Entries)
        {
            if (entry.Kind != EnemyRouteKind.Air) continue;
            List<Tile> tiles = EnemyRoutePath.BuildTiles(cells, entry);
            if (tiles == null) continue;
            if (!region.AirRoutes.TryGetValue(entry.Spawn, out List<List<Vector2Int>> list))
            {
                list = new List<List<Vector2Int>>();
                region.AirRoutes[entry.Spawn] = list;
            }
            list.Add(ToCoords(tiles));
        }
    }

    private static List<Vector2Int> ToCoords(IReadOnlyList<Tile> tiles)
    {
        var coords = new List<Vector2Int>(tiles?.Count ?? 0);
        if (tiles == null) return coords;
        foreach (Tile tile in tiles) coords.Add(tile.Coord);
        return coords;
    }
}
