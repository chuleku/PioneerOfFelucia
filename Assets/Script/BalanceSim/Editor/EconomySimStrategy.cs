using System.Collections.Generic;

// 낮 동안 무엇을 할지 정하는 봇. 한 판 동안 같은 인스턴스를 쓰고, 행동은 log.Actions에 남긴다.
public interface IEconomySimStrategy
{
    string Name { get; }
    bool UsesHeroes { get; }
    void PlayDay(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log);
}

// 영웅 행동을 경제 행동과 어떤 순서로 섞을지.
public enum HeroSpendPolicy
{
    Alternate,      // 영웅 한 번, 경제 한 번 번갈아
    AfterEconomy,   // 경제가 더 할 게 없을 때만 영웅
    BeforeEconomy,  // 영웅을 더 못 만들 때만 경제
    None,           // 영웅 없이 경제만(1단계와 같은 결과 - 회귀 확인용)
}

// 규칙형 봇. 경제: 가중치가 높은 자원 시설을 더 많이 짓고, 빈 일자리 -> 시민 모집 -> 집 순서로 인력을 채운다.
// 영웅: 합성 -> 생성(적은 쪽 종류 먼저) -> 직업 강화 순서. 티어 강화는 인게임에서 쓸 수 없어 넣지 않는다.
public class RuleEconomyStrategy : IEconomySimStrategy
{
    private const int MaxActionsPerDay = 500; // 규칙이 맞물려 끝나지 않는 경우를 막는 안전장치
    private const string MeleeLabel = "근접";
    private const string RangedLabel = "원거리";
    // 영웅 다음 1명 비용의 몇 배 이상 쌓인 자원을 "남는 자원"으로 볼지(재배치 전략).
    private const int SurplusHeroMultiple = 10;

    private readonly Dictionary<ProductionType, int> buildWeights;
    private readonly bool upgradeBeforeBuild;
    private readonly HeroSpendPolicy heroPolicy;
    private readonly bool rebalance;

    public string Name { get; }
    public bool UsesHeroes => heroPolicy != HeroSpendPolicy.None;

    public RuleEconomyStrategy(string name, Dictionary<ProductionType, int> buildWeights, bool upgradeBeforeBuild,
        HeroSpendPolicy heroPolicy, bool rebalance = false)
    {
        Name = name;
        this.buildWeights = buildWeights;
        this.upgradeBeforeBuild = upgradeBeforeBuild;
        this.heroPolicy = heroPolicy;
        this.rebalance = rebalance;
    }

    // 미리 정해둔 비교용 봇들.
    public static List<IEconomySimStrategy> CreatePresets()
    {
        return new List<IEconomySimStrategy>
        {
            new RuleEconomyStrategy("균형", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.Alternate),
            new RuleEconomyStrategy("건설자원 우선", Weights(2, 1, 1, 1, 2), false, HeroSpendPolicy.Alternate),
            new RuleEconomyStrategy("강화 우선", Weights(1, 1, 1, 1, 1), true, HeroSpendPolicy.Alternate),
            new RuleEconomyStrategy("경제 먼저", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.AfterEconomy),
            new RuleEconomyStrategy("영웅 먼저", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.BeforeEconomy),
            new RuleEconomyStrategy("재배치", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.Alternate, rebalance: true),
            new RuleEconomyStrategy("경제만(영웅 없음)", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.None),
        };
    }

    private static Dictionary<ProductionType, int> Weights(int wood, int food, int gold, int iron, int stone)
    {
        return new Dictionary<ProductionType, int>
        {
            [ProductionType.Wood] = wood,
            [ProductionType.Food] = food,
            [ProductionType.Gold] = gold,
            [ProductionType.Iron] = iron,
            [ProductionType.Stone] = stone,
        };
    }

    // 할 수 있는 행동이 없을 때까지 한 가지씩 반복한다. 재배치 전략은 더 할 게 없을 때 하루 한 번 시설을 바꿔 본다.
    public void PlayDay(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        bool rebalancedToday = false;
        for (int step = 0; step < MaxActionsPerDay; step++)
        {
            if (TryOneStep(world, heroes, log)) continue;
            if (!rebalance || rebalancedToday || !TryRebalance(world, heroes, log)) return;
            rebalancedToday = true;
        }
    }

    // 슬롯이 꽉 찼는데 영웅이 어떤 자원에 막혀 있고 다른 자원은 많이 남으면, 남는 쪽 시설 하나를 철거해
    // 막힌 쪽 시설로 바꾼다(철거는 전액 환불이라 손해가 없다). 종류마다 하나는 남기고, 집은 철거할 수 없어 대상이 아니다.
    private static bool TryRebalance(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        if (world.FreeSlots > 0 || !HasEveryFacility(world)) return false;
        ProductionType? shortType = heroes.FirstMissingResource(OccupantKind.MeleeHero);
        if (shortType == null) return false;

        ProductionType? surplusType = null;
        int bestStock = -1;
        foreach (ProductionType type in world.BuildableTypes)
        {
            if (type == shortType.Value || world.FacilityCount(type) < 2) continue;
            int stock = world.Resources.GetAmount(type);
            if (stock < heroes.CostOf(OccupantKind.MeleeHero, type) * SurplusHeroMultiple || stock <= bestStock) continue;
            bestStock = stock;
            surplusType = type;
        }
        if (surplusType == null) return false;

        ProductionFacility target = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (facility.ProductionType != surplusType.Value) continue;
            if (target == null || facility.UpgradeCount < target.UpgradeCount) target = facility;
        }
        if (target == null || !world.TryReplaceFacility(target, shortType.Value)) return false;

        log.Actions.Add($"재배치 {surplusType.Value}→{shortType.Value}");
        return true;
    }

    private bool TryOneStep(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        switch (heroPolicy)
        {
            case HeroSpendPolicy.AfterEconomy:
                return TryEconomyAction(world, log) || TryHeroAction(world, heroes, log);
            case HeroSpendPolicy.BeforeEconomy:
                return TryHeroAction(world, heroes, log) || TryEconomyAction(world, log);
            case HeroSpendPolicy.None:
                return TryEconomyAction(world, log);
            default:
                bool hero = TryHeroAction(world, heroes, log);
                bool economy = TryEconomyAction(world, log);
                return hero || economy;
        }
    }

    // ── 영웅 ──

    private bool TryHeroAction(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        if (!HasEveryFacility(world)) return false;

        if (heroes.TryMergeAny(out BalanceHero merged))
        {
            log.Actions.Add($"합성 → {merged.Data.HeroName}(T{merged.Data.Tier})");
            return true;
        }

        if (TryCreateHero(world, heroes, log)) return true;

        if (heroes.TryClassUpgrade(out int heroType))
        {
            string label = heroType == BalanceHero.RangedHeroType ? RangedLabel : MeleeLabel;
            log.Actions.Add($"{label} 직업 강화 {heroes.ClassUpgrades.GetLevel(heroType)}");
            return true;
        }

        return false;
    }

    // 적은 쪽 종류를 먼저 시도하고, 막히면 다른 종류를 시도한다. 인구가 없으면 한 명 모집해서 바로 쓴다 -
    // 따로 두면 다음 경제 행동이 그 시민을 일터로 데려가 버린다.
    private static bool TryCreateHero(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        OccupantKind first = heroes.CountByKind(OccupantKind.MeleeHero) <= heroes.CountByKind(OccupantKind.RangedHero)
            ? OccupantKind.MeleeHero
            : OccupantKind.RangedHero;
        OccupantKind second = first == OccupantKind.MeleeHero ? OccupantKind.RangedHero : OccupantKind.MeleeHero;

        if (!world.Citizens.CheckCanUseCitizen())
        {
            // 자원이 모자라 어차피 못 만들 때는 시민을 모집하지 않는다.
            if (!heroes.CanAfford(first) && !heroes.CanAfford(second)) return false;
            if (world.Recruit(1) == 0) return false;
            log.Actions.Add("모집 1");
        }

        foreach (OccupantKind kind in new[] { first, second })
        {
            if (!heroes.TryCreate(kind, out BalanceHero created)) continue;
            string label = kind == OccupantKind.RangedHero ? RangedLabel : MeleeLabel;
            log.Actions.Add($"{label} 생성 → {created.Data.HeroName}(T{created.Data.Tier})");
            return true;
        }
        return false;
    }

    // ── 경제 ──

    private bool TryEconomyAction(EconomySimWorld world, EconomySimDayLog log)
    {
        if (world.OpenWorkerSlots > 0 && TryFillWorkers(world, log)) return true;

        if (world.OpenWorkerSlots == 0)
        {
            // 새 시설을 지어도 채울 시민 한도가 없으면 집부터 - 안 그러면 슬롯을 시설로 다 채워 집을 못 짓는다.
            if (LacksCitizenRoom(world) && TryRaiseCitizenCap(world, log)) return true;
            if (upgradeBeforeBuild && HasEveryFacility(world) && TryUpgradeFacility(world, log)) return true;
            if (TryBuildFacility(world, log)) return true;
        }

        return HasEveryFacility(world) && TryUpgradeFacility(world, log);
    }

    // 놀고 있는 시민 배치 -> 모자라면 모집 -> 최대 시민이 꽉 찼으면 집 건설/강화.
    // 영웅이 인구를 티어만큼 써서 놀고 있는 시민 수가 음수일 수 있다.
    private static bool TryFillWorkers(EconomySimWorld world, EconomySimDayLog log)
    {
        int idle = world.Citizens.CanUseCitizen;
        if (idle > 0)
        {
            int assigned = AssignIdle(world, idle);
            if (assigned > 0)
            {
                log.Actions.Add($"배치 {assigned}");
                return true;
            }
        }

        int needed = world.OpenWorkerSlots - System.Math.Min(idle, 0);
        if (world.Citizens.CurrentCitizen < world.Citizens.MaxCitizen)
        {
            int recruited = world.Recruit(needed);
            if (recruited > 0)
            {
                log.Actions.Add($"모집 {recruited}");
                return true;
            }
            return false; // 식량이 모자라 모집을 못 하는 상태 - 집을 더 지어도 소용없다
        }

        return TryRaiseCitizenCap(world, log);
    }

    // 생산 시설을 종류마다 하나씩 다 지었는지(슬롯이 꽉 찼으면 더 지을 수 없으니 다 지은 걸로 본다).
    // 그 전에 영웅·강화에 돌을 써 버리면 채석장을 못 지어 돌 수입이 영영 0으로 굳는다 - 실제 플레이어는 이렇게 하지 않는다.
    private static bool HasEveryFacility(EconomySimWorld world)
    {
        if (world.FreeSlots <= 0) return true;
        foreach (ProductionType type in world.BuildableTypes)
        {
            if (world.FacilityCount(type) == 0) return false;
        }
        return true;
    }

    // 놀고 있는 시민 + 더 모집할 수 있는 수가 새 시설 하나의 일자리보다 적은지.
    private static bool LacksCitizenRoom(EconomySimWorld world)
    {
        int recruitable = world.Citizens.MaxCitizen - world.Citizens.CurrentCitizen;
        return world.Citizens.CanUseCitizen + recruitable < world.NewFacilityWorkers;
    }

    // 집 건설, 슬롯이 없으면 기존 집 강화로 최대 시민을 늘린다.
    private static bool TryRaiseCitizenCap(EconomySimWorld world, EconomySimDayLog log)
    {
        if (world.TryBuildHouse())
        {
            log.Actions.Add("집 건설");
            return true;
        }

        foreach (House house in world.Houses)
        {
            if (!EconomySimWorld.TryUpgrade(house)) continue;
            log.Actions.Add($"집 강화 {house.UpgradeCount}");
            return true;
        }
        return false;
    }

    // 빈 일자리가 있는 시설부터 차례로 채운다.
    private static int AssignIdle(EconomySimWorld world, int idle)
    {
        int total = 0;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (total >= idle) break;
            total += EconomySimWorld.AssignWorkers(facility, idle - total);
        }
        return total;
    }

    // (지은 수 / 가중치)가 가장 작은 종류를 짓는다. 못 지으면 다음 후보로 넘어가지 않는다 -
    // 싼 건물로 새면 가중치 의도가 깨지기 때문이다.
    private bool TryBuildFacility(EconomySimWorld world, EconomySimDayLog log)
    {
        if (world.FreeSlots <= 0) return false;

        ProductionType? pick = null;
        float bestRatio = float.MaxValue;
        foreach (ProductionType type in world.BuildableTypes)
        {
            if (!buildWeights.TryGetValue(type, out int weight) || weight <= 0) continue;
            float ratio = (float)world.FacilityCount(type) / weight;
            if (ratio >= bestRatio) continue;
            bestRatio = ratio;
            pick = type;
        }

        if (pick == null || !world.TryBuildFacility(pick.Value)) return false;
        log.Actions.Add($"{pick.Value} 건설");
        return true;
    }

    // 강화 단계가 가장 낮은 시설부터 강화한다.
    private static bool TryUpgradeFacility(EconomySimWorld world, EconomySimDayLog log)
    {
        ProductionFacility pick = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (facility.UpgradeCount >= facility.MaxUpgrade || !facility.CheckCanUpgrade()) continue;
            if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
        }

        if (pick == null || !EconomySimWorld.TryUpgrade(pick)) return false;
        log.Actions.Add($"{pick.ProductionType} 강화 {pick.UpgradeCount}");
        return true;
    }
}
