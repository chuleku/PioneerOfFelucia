using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 경제 시뮬레이터가 읽는 에셋 묶음. 값을 복사해 두지 않고 런타임이 쓰는 SO·프리팹 참조를 그대로 들고 있는다.
public class EconomySimAssets
{
    public readonly List<ProductionValue> Facilities = new();
    public readonly List<int> RegionSlotCounts = new();
    public readonly List<BaseUpgradeData> StartingResourceUpgrades = new();
    public readonly List<BaseUpgradeData> MaxCitizenUpgrades = new();
    public HouseConfig House;
    public ProductionEconomyConfig EconomyConfig;
    public ResourcesManager ResourcesPrefab;
    public CitizenManager CitizenPrefab;
    public int CitizenFoodCost;

    public bool IsValid => Facilities.Count > 0 && House != null && EconomyConfig != null &&
        ResourcesPrefab != null && CitizenPrefab != null && RegionSlotCounts.Count > 0 && CitizenFoodCost > 0;
}

// 프로젝트에서 경제 관련 에셋을 찾아 EconomySimAssets로 모은다. 계산은 하지 않는다.
public static class EconomySimSource
{
    private const string ProductionValueFilter = "t:ProductionValue";
    private const string HouseConfigFilter = "t:HouseConfig";
    private const string EconomyConfigFilter = "t:ProductionEconomyConfig";
    private const string ResourcesPrefabFilter = "ResourcesManager t:Prefab";
    private const string CitizenPrefabFilter = "CitizenManager t:Prefab";
    private const string BasePanelPrefabFilter = "BasePanel t:Prefab";

    // 런타임 필드 이름. 인스펙터 전용(private) 값이라 SerializedObject로만 읽을 수 있다.
    private const string CitizenCostField = "costAmount";
    private const string RegionsField = "regions";
    private const string SlotCountField = "slotCount";
    private const string StartingResourceUpgradesField = "startingResourceUpgrades";
    private const string MaxCitizenUpgradesField = "maxCitizenUpgrades";

    // 에셋을 전부 다시 읽는다. 하나라도 못 찾으면 IsValid가 false가 된다.
    public static EconomySimAssets Load()
    {
        var assets = new EconomySimAssets
        {
            House = LoadSingle<HouseConfig>(HouseConfigFilter),
            EconomyConfig = LoadSingle<ProductionEconomyConfig>(EconomyConfigFilter),
            ResourcesPrefab = LoadPrefabComponent<ResourcesManager>(ResourcesPrefabFilter),
            CitizenPrefab = LoadPrefabComponent<CitizenManager>(CitizenPrefabFilter),
        };

        CollectFacilities(assets.Facilities);
        CollectBasePanelValues(assets);
        CollectUpgradeList(assets.ResourcesPrefab, StartingResourceUpgradesField, assets.StartingResourceUpgrades);
        CollectUpgradeList(assets.CitizenPrefab, MaxCitizenUpgradesField, assets.MaxCitizenUpgrades);
        return assets;
    }

    // 생산 시설 SO를 자원 종류 순서로 모은다.
    private static void CollectFacilities(List<ProductionValue> facilities)
    {
        foreach (string guid in AssetDatabase.FindAssets(ProductionValueFilter))
        {
            var value = AssetDatabase.LoadAssetAtPath<ProductionValue>(AssetDatabase.GUIDToAssetPath(guid));
            if (value != null) facilities.Add(value);
        }
        facilities.Sort((left, right) => left.Type.CompareTo(right.Type));
    }

    // 시민 모집 단가(AddCitizen)와 지역별 슬롯 수(RegionOverviewPanel)는 BasePanel 프리팹 안에 있다.
    private static void CollectBasePanelValues(EconomySimAssets assets)
    {
        foreach (string guid in AssetDatabase.FindAssets(BasePanelPrefabFilter))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null) continue;

            var addCitizen = prefab.GetComponentInChildren<AddCitizen>(true);
            var overview = prefab.GetComponentInChildren<RegionOverviewPanel>(true);
            if (addCitizen == null || overview == null) continue;

            assets.CitizenFoodCost = new SerializedObject(addCitizen).FindProperty(CitizenCostField).intValue;
            SerializedProperty regions = new SerializedObject(overview).FindProperty(RegionsField);
            for (int index = 0; index < regions.arraySize; index++)
            {
                assets.RegionSlotCounts.Add(regions.GetArrayElementAtIndex(index).FindPropertyRelative(SlotCountField).intValue);
            }
            return;
        }
    }

    // 프리팹 컴포넌트에 직렬화된 타이틀 강화 목록을 저작 순서 그대로 읽는다.
    private static void CollectUpgradeList(Object owner, string fieldName, List<BaseUpgradeData> result)
    {
        if (owner == null) return;
        SerializedProperty list = new SerializedObject(owner).FindProperty(fieldName);
        for (int index = 0; index < list.arraySize; index++)
        {
            if (list.GetArrayElementAtIndex(index).objectReferenceValue is BaseUpgradeData upgrade) result.Add(upgrade);
        }
    }

    private static T LoadSingle<T>(string filter) where T : Object
    {
        string[] guids = AssetDatabase.FindAssets(filter);
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    // 이름으로 찾은 프리팹 중 해당 컴포넌트를 가진 첫 번째 것을 쓴다.
    private static T LoadPrefabComponent<T>(string filter) where T : Component
    {
        foreach (string guid in AssetDatabase.FindAssets(filter))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab != null && prefab.TryGetComponent(out T component)) return component;
        }
        return null;
    }
}
