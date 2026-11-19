using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 영웅 생성·합성·강화에 쓰는 값 묶음. MainScene이 프리팹 값을 덮어쓰고 있어서(예: 생성가 점증률)
// 씬 인스턴스에서 읽어 일반 데이터로 옮겨 둔다. 씬을 닫은 뒤에도 쓸 수 있도록 컴포넌트 참조는 들고 있지 않는다.
public class BalanceHeroAssets
{
    public readonly List<HeroData> Pool = new();                 // HeroRegistry.datas 중 프리팹이 있는 것(런타임 후보 풀과 같은 순서)
    public readonly List<List<int>> TierWeightsByStage = new();  // HeroCreateManager.probabilityByStage
    public readonly List<ResourceCost> MeleeCost = new();
    public readonly List<ResourceCost> RangedCost = new();
    public readonly List<BaseUpgradeData> CreateCostUpgrades = new();
    public readonly Dictionary<HeroData, HeroSimEntry> StatEntries = new();
    public bool UseDailyEscalation;
    public float DailyIncreaseRate;
    public bool UseRegionEscalation;
    public float RegionIncreaseRate;
    public HeroClassUpgradeConfig ClassConfig;
    public HeroUpgradeConfig TierConfig;
    public string SourceNote = string.Empty;

    public bool IsValid => Pool.Count > 0 && TierWeightsByStage.Count > 0 && MeleeCost.Count > 0 &&
        RangedCost.Count > 0 && ClassConfig != null && TierConfig != null;
}

// MainScene(BalanceSceneReader가 열어 준 씬)에서 영웅 관련 값을 읽는다.
public static class BalanceHeroSource
{
    // 런타임 필드 이름. 인스펙터 전용(private) 값이라 SerializedObject로만 읽을 수 있다.
    private const string MeleeIconField = "meleeIcon";
    private const string RangedIconField = "rangedIcon";
    private const string CostUpgradesField = "costUpgrades";
    private const string UseDailyField = "useEscalatingHeroPrice";
    private const string DailyRateField = "heroPriceIncreaseRate";
    private const string UseRegionField = "useRegionHeroPrice";
    private const string RegionRateField = "regionPriceIncreaseRate";
    private const string StagesField = "probabilityByStage";
    private const string TierWeightsField = "tierWeights";
    private const string DatasField = "datas";

    // 씬과 상관없는 에셋(강화 설정, 영웅 스탯)만 채운 빈 묶음. 씬 값은 ReadScene이 채운다.
    public static BalanceHeroAssets CreateWithAssets()
    {
        var assets = new BalanceHeroAssets
        {
            ClassConfig = HeroSimSource.LoadClassConfig(),
            TierConfig = HeroSimSource.LoadTierConfig(),
        };
        foreach (HeroSimEntry entry in HeroSimSource.CollectEntries())
        {
            assets.StatEntries[entry.HeroData] = entry;
        }
        return assets;
    }

    // FindObjectsOfType은 미리보기 씬을 뒤지지 않으므로 루트부터 직접 훑는다.
    public static void ReadScene(Scene scene, BalanceHeroAssets assets)
    {
        HeroSetPanel setPanel = null;
        HeroCreateManager createManager = null;
        HeroRegistry registry = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (setPanel == null) setPanel = root.GetComponentInChildren<HeroSetPanel>(true);
            if (createManager == null) createManager = root.GetComponentInChildren<HeroCreateManager>(true);
            if (registry == null) registry = root.GetComponentInChildren<HeroRegistry>(true);
        }

        if (setPanel != null) ReadSetPanel(setPanel, assets);
        if (createManager != null) ReadTierWeights(createManager, assets);
        if (registry != null) ReadPool(registry, assets);
    }

    private static void ReadSetPanel(HeroSetPanel setPanel, BalanceHeroAssets assets)
    {
        var serialized = new SerializedObject(setPanel);
        CopyCost(serialized.FindProperty(MeleeIconField), assets.MeleeCost);
        CopyCost(serialized.FindProperty(RangedIconField), assets.RangedCost);
        assets.UseDailyEscalation = serialized.FindProperty(UseDailyField).boolValue;
        assets.DailyIncreaseRate = serialized.FindProperty(DailyRateField).floatValue;
        assets.UseRegionEscalation = serialized.FindProperty(UseRegionField).boolValue;
        assets.RegionIncreaseRate = serialized.FindProperty(RegionRateField).floatValue;

        SerializedProperty upgrades = serialized.FindProperty(CostUpgradesField);
        for (int index = 0; index < upgrades.arraySize; index++)
        {
            if (upgrades.GetArrayElementAtIndex(index).objectReferenceValue is BaseUpgradeData upgrade)
                assets.CreateCostUpgrades.Add(upgrade);
        }
    }

    private static void CopyCost(SerializedProperty iconProperty, List<ResourceCost> result)
    {
        if (iconProperty.objectReferenceValue is HeroCreateIcon icon) result.AddRange(icon.ResourceCost);
    }

    private static void ReadTierWeights(HeroCreateManager createManager, BalanceHeroAssets assets)
    {
        SerializedProperty stages = new SerializedObject(createManager).FindProperty(StagesField);
        for (int stage = 0; stage < stages.arraySize; stage++)
        {
            SerializedProperty weights = stages.GetArrayElementAtIndex(stage).FindPropertyRelative(TierWeightsField);
            var row = new List<int>(weights.arraySize);
            for (int index = 0; index < weights.arraySize; index++)
            {
                row.Add(weights.GetArrayElementAtIndex(index).intValue);
            }
            assets.TierWeightsByStage.Add(row);
        }
    }

    // HeroRegistry.Awake와 같은 기준 - 프리팹 참조가 끊긴 데이터는 후보에서 뺀다.
    private static void ReadPool(HeroRegistry registry, BalanceHeroAssets assets)
    {
        SerializedProperty datas = new SerializedObject(registry).FindProperty(DatasField);
        for (int index = 0; index < datas.arraySize; index++)
        {
            if (datas.GetArrayElementAtIndex(index).objectReferenceValue is HeroData data && data.HeroPrefab != null)
                assets.Pool.Add(data);
        }
    }
}
