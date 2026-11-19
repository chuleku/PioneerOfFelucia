using System.Collections.Generic;
using UnityEngine;

// 보유 영웅 한 명. 배치(필드 위치)는 3단계(전투)에서 다룬다 - 여기선 로스터에만 있다.
public class BalanceHero
{
    public HeroData Data;
    public int CitizenCost;
    public OccupantKind Kind => Data.HeroType == RangedHeroType ? OccupantKind.RangedHero : OccupantKind.MeleeHero;

    public const int MeleeHeroType = 0;
    public const int RangedHeroType = 1;
}

// 영웅 생성·합성·강화. 직업 강화는 런타임 HeroClassUpgradeState를 그대로 쓰고(티어 강화는 인게임에서 쓸 수 없어 뺐다),
// 생성·합성은 UI MonoBehaviour(HeroSetPanel/HeroCreateManager/HeroCombineManager) 안에 있어
// 계산만 옮겨 왔다 - 옮긴 곳마다 원본 위치를 적어 둔다. 원본이 바뀌면 여기도 같이 고쳐야 한다.
public class BalanceHeroWorld
{
    private const int MergeCount = 3;

    private readonly BalanceHeroAssets assets;
    private readonly EconomySimWorld economy;
    private readonly UpgradeState upgradeState;
    private readonly string seed;
    private readonly Dictionary<(OccupantKind Kind, int Tier), List<HeroData>> poolByTierAndKind = new();
    private readonly Dictionary<(HeroData Data, int ClassLevel), HeroSimResult> powerCache;
    private readonly Dictionary<(OccupantKind Kind, int Tier), int> combineCounts = new();
    private int meleeDraws;
    private int rangedDraws;

    public HeroClassUpgradeState ClassUpgrades { get; }
    public List<BalanceHero> Roster { get; } = new();
    public int CreatedToday { get; private set; }

    public BalanceHeroWorld(BalanceHeroAssets assets, EconomySimWorld economy, UpgradeState upgradeState, string seed,
        Dictionary<(HeroData, int), HeroSimResult> powerCache)
    {
        this.assets = assets;
        this.economy = economy;
        this.upgradeState = upgradeState;
        this.seed = seed;
        this.powerCache = powerCache;
        ClassUpgrades = new HeroClassUpgradeState(assets.ClassConfig, economy.Resources, upgradeState);

        // HeroRegistry.Awake의 (티어, 종류) 인덱스와 같은 순서로 묶는다 - 뽑기 결과가 순서에 달려 있다.
        foreach (HeroData data in assets.Pool)
        {
            var key = (KindOf(data), data.Tier);
            if (!poolByTierAndKind.TryGetValue(key, out List<HeroData> list))
            {
                list = new List<HeroData>();
                poolByTierAndKind[key] = list;
            }
            list.Add(data);
        }
    }

    // 게임 시작 이후 새로 열린 지역 수. 생성가 지역 점증과 티어 확률 행이 둘 다 이 값을 쓴다.
    public int ExtraRegions => economy.UnlockedRegionCount - EconomySimRunner.StartingRegions;

    public void StartDay() => CreatedToday = 0;

    public int CountByKind(OccupantKind kind) => Roster.FindAll(hero => hero.Kind == kind).Count;

    public int PoolSize(OccupantKind kind, int tier) =>
        poolByTierAndKind.TryGetValue((kind, tier), out List<HeroData> list) ? list.Count : 0;

    // HeroSetPanel.GetCost + GetEscalatedUnitCost와 같은 계산: 타이틀 할인 후 (1 + 오늘 점증 + 지역 점증) 배율.
    public (ProductionType Type, int Amount)[] GetNextCost(OccupantKind kind)
    {
        List<ResourceCost> source = kind == OccupantKind.RangedHero ? assets.RangedCost : assets.MeleeCost;
        float discount = upgradeState.GetTotalEffect(assets.CreateCostUpgrades);
        var baseCost = source.ToNegatedCostArray().ApplyDiscount(discount);

        float multiplier = 1f;
        if (assets.UseDailyEscalation) multiplier += assets.DailyIncreaseRate * CreatedToday;
        if (assets.UseRegionEscalation) multiplier += assets.RegionIncreaseRate * ExtraRegions;
        if (multiplier == 1f) return baseCost;
        return baseCost.Scale(multiplier);
    }

    // 지금 하나 더 만들 수 없는 이유. HeroSetPanel.GetUnaffordReason과 같은 순서(인구 -> 자원)지만
    // 인구에 막혀도 뒤에 숨은 자원 부족까지 "인구+Wood·Gold"처럼 모자란 자원을 전부 같이 적는다. 만들 수 있으면 null.
    public string GetBlockReason(OccupantKind kind)
    {
        var missing = new List<string>();
        foreach (var (type, amount) in GetNextCost(kind))
        {
            if (amount < 0 && economy.Resources.GetAmount(type) < -amount) missing.Add(type.ToString());
        }

        string resources = missing.Count > 0 ? string.Join("·", missing) : null;
        if (economy.Citizens.CheckCanUseCitizen()) return resources;
        return resources == null ? BalanceBlockReason.Population : $"{BalanceBlockReason.Population}+{resources}";
    }

    // 인구는 빼고 자원만 보면 하나 더 만들 수 있는지.
    public bool CanAfford(OccupantKind kind) => economy.Resources.CheckResources(GetNextCost(kind));

    // HeroSetPanel.BulkCreate의 1회분: 비용 차감 -> 뽑기 -> 뽑힌 티어만큼 인구 사용(음수까지 내려갈 수 있음).
    public bool TryCreate(OccupantKind kind, out BalanceHero created)
    {
        created = null;
        if (GetBlockReason(kind) != null) return false;

        var cost = GetNextCost(kind);
        economy.Resources.ProductChanged(cost);
        if (!TryRoll(kind, out HeroData picked))
        {
            economy.Resources.ProductChanged(cost.Multiply(-1));
            return false;
        }

        economy.Citizens.UseCitizenForHero(picked.PopulationCost);
        created = new BalanceHero { Data = picked, CitizenCost = picked.PopulationCost };
        Roster.Add(created);
        CreatedToday++;
        return true;
    }

    // HeroCombineManager.Combine: 같은 MergeKey(UnitId+티어) 3개 -> 같은 종류 다음 티어 중 하나.
    // 원본 3개의 인구를 돌려받고 결과 티어만큼 다시 쓴다. 로스터 앞에서부터 처음 모인 묶음 하나만 합성한다.
    public bool TryMergeAny(out BalanceHero merged)
    {
        merged = null;
        List<BalanceHero> group = FindMergeGroup(out List<HeroData> nextTier);
        if (group == null) return false;

        OccupantKind kind = group[0].Kind;
        int tier = group[0].Data.Tier;
        System.Random rng = new System.Random(GameSeeding.Derive(seed, ConsumeCombine(kind, tier)));
        HeroData next = nextTier[rng.Next(nextTier.Count)];

        int refund = 0;
        foreach (BalanceHero member in group)
        {
            refund += member.CitizenCost;
            Roster.Remove(member);
        }
        economy.Citizens.FreeCitizenForHero(refund);
        economy.Citizens.UseCitizenForHero(next.PopulationCost);
        merged = new BalanceHero { Data = next, CitizenCost = next.PopulationCost };
        Roster.Add(merged);
        return true;
    }

    // 다음 티어 데이터가 있는 MergeKey 중 처음으로 3개가 모인 묶음.
    private List<BalanceHero> FindMergeGroup(out List<HeroData> nextTier)
    {
        nextTier = null;
        var groups = new Dictionary<MergeKey, List<BalanceHero>>();
        foreach (BalanceHero hero in Roster)
        {
            if (!groups.TryGetValue(hero.Data.MergeKey, out List<BalanceHero> group))
            {
                group = new List<BalanceHero>();
                groups[hero.Data.MergeKey] = group;
            }
            group.Add(hero);
            if (group.Count < MergeCount) continue;
            if (poolByTierAndKind.TryGetValue((hero.Kind, hero.Data.Tier + 1), out nextTier)) return group;
        }
        return null;
    }

    // 보유한 종류 중 레벨이 가장 낮은 직업을 강화한다.
    public bool TryClassUpgrade(out int heroType)
    {
        heroType = -1;
        int bestLevel = int.MaxValue;
        foreach (BalanceHero hero in Roster)
        {
            int type = hero.Data.HeroType;
            int level = ClassUpgrades.GetLevel(type);
            if (level >= bestLevel || !ClassUpgrades.CanLevelUp(type)) continue;
            if (!economy.Resources.CheckResources(ClassUpgrades.GetNextLevelCost(type))) continue;
            bestLevel = level;
            heroType = type;
        }
        return heroType >= 0 && ClassUpgrades.TryLevelUp(heroType);
    }

    // 보유 영웅 전원의 초당 피해·체력 합계(방어 0 기준, 배치·시너지 미반영). 영웅 시뮬레이터(HeroSimBuilder)와 같은 계산.
    public (float Dps, float Hp) CalculatePower()
    {
        float dps = 0f;
        float hp = 0f;
        foreach (BalanceHero hero in Roster)
        {
            if (!assets.StatEntries.TryGetValue(hero.Data, out HeroSimEntry entry)) continue;
            int classLevel = ClassUpgrades.GetLevel(hero.Data.HeroType);
            var key = (hero.Data, classLevel);
            if (!powerCache.TryGetValue(key, out HeroSimResult result))
            {
                var input = new HeroSimInput { ClassUpgradeCount = classLevel };
                result = HeroSimBuilder.BuildResult(entry, assets.TierConfig, assets.ClassConfig, input, 0f, 0, 0f);
                powerCache[key] = result;
            }
            dps += result.DamagePerSecond;
            hp += result.MaxHp;
        }
        return (dps, hp);
    }

    // HeroCreateManager.TryRollHero: 지역 진행도 행의 가중치로 티어를 뽑고, 그 티어+종류 중 하나를 고른다.
    // 그 티어 데이터가 없으면 한 티어씩 내려가며 다시 찾는다. 시드는 GameManager.ConsumeHeroDraw처럼 종류별 순번.
    private bool TryRoll(OccupantKind kind, out HeroData result)
    {
        result = null;
        int stage = Mathf.Clamp(ExtraRegions, 0, assets.TierWeightsByStage.Count - 1);
        List<int> weights = assets.TierWeightsByStage[stage];
        if (weights.Count == 0) return false;

        int count = kind == OccupantKind.RangedHero ? ++rangedDraws : ++meleeDraws;
        System.Random rng = new System.Random(GameSeeding.Derive(seed, count));

        for (int tier = WeightedPickTier(weights, rng); tier >= 1; tier--)
        {
            if (!poolByTierAndKind.TryGetValue((kind, tier), out List<HeroData> candidates)) continue;
            result = candidates[rng.Next(candidates.Count)];
            return true;
        }
        return false;
    }

    // HeroCreateManager.WeightedPickTier 그대로.
    private static int WeightedPickTier(List<int> weights, System.Random rng)
    {
        int total = 0;
        foreach (int weight in weights) total += Mathf.Max(0, weight);
        if (total <= 0) return 1;

        int roll = rng.Next(total);
        int cumulative = 0;
        for (int index = 0; index < weights.Count; index++)
        {
            cumulative += Mathf.Max(0, weights[index]);
            if (roll < cumulative) return index + 1;
        }
        return weights.Count;
    }

    // GameManager.ConsumeHeroCombine: (종류, 티어)별 순번을 하나 늘려 돌려준다.
    private int ConsumeCombine(OccupantKind kind, int tier)
    {
        combineCounts.TryGetValue((kind, tier), out int count);
        count++;
        combineCounts[(kind, tier)] = count;
        return count;
    }

    private static OccupantKind KindOf(HeroData data) =>
        data.HeroType == BalanceHero.RangedHeroType ? OccupantKind.RangedHero : OccupantKind.MeleeHero;
}

// 영웅 생성이 막힌 이유 표기. 자원 부족은 ProductionType 이름을 그대로 쓴다.
public static class BalanceBlockReason
{
    public const string Population = "인구";
}
