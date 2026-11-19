using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// 시뮬레이터 한 판의 경제 상태. 공식은 새로 쓰지 않고 런타임 클래스(ResourcesManager/CitizenManager/
// BaseConstructor/ProductionFacility/House/FacilityManager)를 숨김 오브젝트 위에서 그대로 돌린다.
// 다 쓰면 반드시 Dispose해서 숨김 오브젝트를 지운다.
public class EconomySimWorld : IDisposable
{
    private const string HiddenObjectName = "EconomySimWorld";
    private const string InjectMethodName = "Construct"; // VContainer [Inject] 메서드 - 에디터에선 직접 불러준다
    private const string RegionJsonFormat = "{{\"moduleId\":{0},\"slotCount\":{1}}}";

    private readonly GameObject hiddenObject;
    private readonly FacilityManager facilityManager = new();
    private readonly BaseConstructor constructor;
    private readonly List<RegionFacilitySlots> regions = new();
    private readonly Dictionary<ProductionType, BuildableFacility> facilityOptions = new();
    private readonly BuildableFacility houseOption;

    public ResourcesManager Resources { get; }
    public CitizenManager Citizens { get; }
    public int CitizenFoodCost { get; }
    public int UnlockedRegionCount { get; private set; }
    public List<ProductionFacility> Facilities { get; } = new();
    public List<House> Houses { get; } = new();
    public IEnumerable<ProductionType> BuildableTypes => facilityOptions.Keys;

    public EconomySimWorld(EconomySimAssets assets, UpgradeState upgradeState)
    {
        CitizenFoodCost = assets.CitizenFoodCost;
        hiddenObject = EditorUtility.CreateGameObjectWithHideFlags(HiddenObjectName, HideFlags.HideAndDontSave);
        Resources = CloneComponent(assets.ResourcesPrefab, upgradeState);
        Citizens = CloneComponent(assets.CitizenPrefab, upgradeState);
        constructor = new BaseConstructor(Resources, Citizens, facilityManager, upgradeState, assets.EconomyConfig);

        foreach (ProductionValue value in assets.Facilities)
        {
            facilityOptions[value.Type] = new BuildableFacility { kind = OccupantKind.Resource, facilityValue = value };
            NewFacilityWorkers = Mathf.Min(NewFacilityWorkers, value.DefaultMaxWorker);
        }
        houseOption = new BuildableFacility { kind = OccupantKind.Building, houseConfig = assets.House };

        // 프리팹의 RegionFacilitySlots는 에셋 쪽에 슬롯을 캐시하므로 건드리지 않고, 같은 값으로 새로 만든다.
        for (int index = 0; index < assets.RegionSlotCounts.Count; index++)
        {
            string json = string.Format(RegionJsonFormat, index + 1, assets.RegionSlotCounts[index]);
            regions.Add(JsonUtility.FromJson<RegionFacilitySlots>(json));
        }
    }

    // 새로 지은 시설 하나가 처음 여는 일자리 수(시설마다 다르면 가장 작은 값).
    public int NewFacilityWorkers { get; private set; } = int.MaxValue;
    public int RegionCount => regions.Count;
    public int OpenWorkerSlots => SumFacilities(facility => facility.MaxWorker - facility.WorkerAmount);
    public int AssignedWorkers => SumFacilities(facility => facility.WorkerAmount);

    public int TotalSlots => CountSlots(_ => true);
    public int FreeSlots => CountSlots(slot => slot.IsEmpty);

    public int FacilityCount(ProductionType type) => Facilities.FindAll(facility => facility.ProductionType == type).Count;

    // 해금 지역 수를 맞춘다. 줄이는 일은 없다(게임에서도 지역은 닫히지 않는다).
    public void SetUnlockedRegions(int count)
    {
        UnlockedRegionCount = Mathf.Clamp(count, UnlockedRegionCount, regions.Count);
    }

    public bool TryBuildFacility(ProductionType type)
    {
        if (!facilityOptions.TryGetValue(type, out BuildableFacility option)) return false;
        if (!TryBuildInFreeSlot(option, out object built)) return false;
        Facilities.Add((ProductionFacility)built);
        return true;
    }

    // 시설 하나를 철거하고(BaseConstructor.Demolish - 건설비·강화비 전액 환불, 일꾼 반납) 그 자리에 다른 종류를 짓는다.
    // 환불받은 건설비로 같은 값의 새 시설을 지을 수 있다. 집은 게임에서 철거할 수 없어 받지 않는다.
    public bool TryReplaceFacility(ProductionFacility target, ProductionType newType)
    {
        for (int regionIndex = 0; regionIndex < UnlockedRegionCount; regionIndex++)
        {
            RegionFacilitySlots region = regions[regionIndex];
            for (int slotIndex = 0; slotIndex < region.Slots.Count; slotIndex++)
            {
                if (region.Slots[slotIndex].Occupant != target) continue;
                constructor.Demolish(region, slotIndex);
                Facilities.Remove(target);
                return TryBuildFacility(newType);
            }
        }
        return false;
    }

    // 펠리움 1개를 써서 원하는 자원을 tradeAmount(프리팹 300)만큼 받는다 - ResourcesManager.TradeResource 그대로.
    // TradeResource는 보유량을 검사하지 않아(버튼이 막는다) 여기서 먼저 확인한다.
    public bool TryTrade(ProductionType type)
    {
        if (Resources.Special <= 0) return false;
        Resources.TradeResource(type);
        return true;
    }

    public void GrantSpecial(int amount)
    {
        for (int count = 0; count < amount; count++) Resources.GetSpecial();
    }

    public bool TryBuildHouse()
    {
        if (!TryBuildInFreeSlot(houseOption, out object built)) return false;
        Houses.Add((House)built);
        return true;
    }

    // Upgrade()는 비용을 검사하지 않고 차감하므로 반드시 CheckCanUpgrade로 먼저 거른다(UI와 같은 순서).
    public static bool TryUpgrade(IUpgradableOccupant occupant)
    {
        if (!occupant.CheckCanUpgrade()) return false;
        occupant.Upgrade();
        return true;
    }

    // AddCitizen.CreateCitizen과 같은 검사·차감. 식량이 모자라면 살 수 있는 만큼만 모집한다.
    public int Recruit(int wanted)
    {
        int capacity = Citizens.MaxCitizen - Citizens.CurrentCitizen;
        int affordable = Resources.Food / CitizenFoodCost;
        int amount = Mathf.Min(wanted, Mathf.Min(capacity, affordable));
        if (amount <= 0) return 0;

        var cost = new (ProductionType Type, int Amount)[] { (ProductionType.Food, amount * -CitizenFoodCost) };
        if (!Citizens.CheckCanIncreaseCitizen(amount) || !Resources.CheckResources(cost)) return 0;

        Citizens.IncreaseCitizen(amount);
        Resources.ProductChanged(cost);
        return amount;
    }

    // IncreaseWorker는 실패해도 조용히 넘어가므로 실제로 늘어난 인원을 세서 돌려준다.
    public static int AssignWorkers(ProductionFacility facility, int wanted)
    {
        int before = facility.WorkerAmount;
        for (int count = 0; count < wanted; count++)
        {
            facility.IncreaseWorker();
        }
        return facility.WorkerAmount - before;
    }

    // 밤이 끝날 때의 생산 지급. FacilityManager.SumProduct는 주입된 ResourcesManager가 필요해서
    // 같은 합산(GetTotalProduction)만 빌려 쓰고 지급은 여기서 한다.
    public (ProductionType Type, int Amount)[] PayOut()
    {
        var totals = facilityManager.GetTotalProduction();
        Resources.ProductChanged(totals);
        return totals;
    }

    public void Dispose()
    {
        if (hiddenObject != null) Object.DestroyImmediate(hiddenObject);
    }

    // 프리팹의 직렬화 값(시작 자원·시민 수 등)을 숨김 오브젝트로 복사하고, 에디터에선 VContainer가 없으니
    // [Inject] Construct를 직접 불러 타이틀 보너스까지 런타임과 같은 코드로 얹는다.
    private T CloneComponent<T>(T prefabComponent, UpgradeState upgradeState) where T : Component
    {
        T component = hiddenObject.AddComponent<T>();
        EditorUtility.CopySerialized(prefabComponent, component);

        MethodInfo construct = typeof(T).GetMethod(InjectMethodName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (construct == null) throw new MissingMethodException(typeof(T).Name, InjectMethodName);
        construct.Invoke(component, new object[] { upgradeState });
        return component;
    }

    private bool TryBuildInFreeSlot(BuildableFacility option, out object built)
    {
        built = null;
        for (int regionIndex = 0; regionIndex < UnlockedRegionCount; regionIndex++)
        {
            RegionFacilitySlots region = regions[regionIndex];
            for (int slotIndex = 0; slotIndex < region.Slots.Count; slotIndex++)
            {
                if (!region.Slots[slotIndex].IsEmpty) continue;
                return constructor.TryBuild(option, region, slotIndex, out built);
            }
        }
        return false;
    }

    private int CountSlots(Predicate<RegionFacilitySlot> match)
    {
        int count = 0;
        for (int regionIndex = 0; regionIndex < UnlockedRegionCount; regionIndex++)
        {
            foreach (RegionFacilitySlot slot in regions[regionIndex].Slots)
            {
                if (match(slot)) count++;
            }
        }
        return count;
    }

    private int SumFacilities(Func<ProductionFacility, int> selector)
    {
        int total = 0;
        foreach (ProductionFacility facility in Facilities) total += selector(facility);
        return total;
    }
}
