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
// 영웅: 합성 -> 생성(근접은 지역당 막을 만큼, 나머지는 원거리) -> 직업 강화 순서. 티어 강화는 인게임에서 쓸 수 없어 넣지 않는다.
public class RuleEconomyStrategy : IEconomySimStrategy
{
    private const int MaxActionsPerDay = 500; // 규칙이 맞물려 끝나지 않는 경우를 막는 안전장치
    private const string MeleeLabel = "근접";
    private const string RangedLabel = "원거리";
    // 영웅 다음 1명 비용의 몇 배 이상 쌓인 자원을 "남는 자원"으로 볼지(재배치 전략).
    private const int SurplusHeroMultiple = 10;
    // 열린 지역 하나에 둘 근접 전력(티어 합). 이만큼 채우면 그다음부터는 원거리를 먼저 뽑는다(기록: 10일차 1지역 근접 7명, 대부분 T1).
    private const int MeleePerRegion = 6;
    // 목재·석재 강화를 영웅보다 먼저 하기 전에 갖춰 둘 영웅 수(기록: 1일차에도 근접 2명은 뽑았다 - 첫 밤을 맨몸으로 맞지 않게).
    private const int MinHeroesBeforeCore = 2;
    // 실제 플레이 방식의 직업 강화 목표 단계 = 일차 × 이 값(반올림).
    private const float ClassLevelPerDay = 0.42f;

    private readonly Dictionary<ProductionType, int> buildWeights;
    private readonly bool upgradeBeforeBuild;
    private readonly HeroSpendPolicy heroPolicy;
    private readonly bool rebalance;
    // 실제 플레이 방식(1~12일차 기록): 같은 수면 원거리를 먼저 뽑고, 강화는 남은 양이 적은 자원 시설부터(목재·석재를 고르게),
    // 펠리움은 지을 것·뽑을 것 하나를 끝까지 채울 만큼만 바꾼다.
    private readonly bool playerStyle;

    public string Name { get; }
    public bool UsesHeroes => heroPolicy != HeroSpendPolicy.None;

    public RuleEconomyStrategy(string name, Dictionary<ProductionType, int> buildWeights, bool upgradeBeforeBuild,
        HeroSpendPolicy heroPolicy, bool rebalance = false, bool playerStyle = false)
    {
        Name = name;
        this.buildWeights = buildWeights;
        this.upgradeBeforeBuild = upgradeBeforeBuild;
        this.heroPolicy = heroPolicy;
        this.rebalance = rebalance;
        this.playerStyle = playerStyle;
    }

    // 미리 정해둔 비교용 봇들.
    public static List<IEconomySimStrategy> CreatePresets()
    {
        // 기준점: 플레이 기록기 파일(Logs/PlayRecords, 최근 3개)과 손으로 정리한 기록. 기록이 끝난 날부터는 균형 봇이 이어 받는다.
        var presets = new List<IEconomySimStrategy>(ReplayEconomyStrategy.FromRecords(CreateBalanced));
        presets.AddRange(new List<IEconomySimStrategy>
        {
            ReplayEconomyStrategy.CreateUserPlay(CreateBalanced()),
            CreateBalanced(),
            new RuleEconomyStrategy("건설자원 우선", Weights(2, 1, 1, 1, 2), false, HeroSpendPolicy.Alternate),
            new RuleEconomyStrategy("강화 우선", Weights(1, 1, 1, 1, 1), true, HeroSpendPolicy.Alternate),
            new RuleEconomyStrategy("경제 먼저", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.AfterEconomy),
            new RuleEconomyStrategy("영웅 먼저", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.BeforeEconomy),
            new RuleEconomyStrategy("재배치", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.Alternate, rebalance: true),
            new RuleEconomyStrategy("경제만(영웅 없음)", Weights(1, 1, 1, 1, 1), false, HeroSpendPolicy.None),
        });
        return presets;
    }

    // 균형 = 실제 플레이 방식. 가중치는 1~2지역을 다 채웠을 때의 실제 구성(목재·석재·금 2, 철 3, 식량 1 - 2지역은 식량 빼고 철 2개).
    private static RuleEconomyStrategy CreateBalanced() =>
        new("균형", Weights(2, 1, 2, 3, 2), false, HeroSpendPolicy.Alternate, playerStyle: true);

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
            if (rebalance && !rebalancedToday && TryRebalance(world, heroes, log))
            {
                rebalancedToday = true;
                continue;
            }
            if (!TrySpendFelium(world, heroes, log)) return;
        }
    }

    // 더 할 게 없을 때 펠리움 1개를 막힌 자원으로 바꾼다(+300). 영웅을 쓰는 전략은 영웅 다음 1명에 처음 모자란 자원,
    // 영웅이 막히지 않았거나 경제만 하는 전략은 금을 뺀 자원 중 가장 적은 것(금은 영웅에만 쓰여 경제엔 소용없다).
    // 인구에만 막혔으면 자원을 받아도 소용없으니 쓰지 않는다.
    private bool TrySpendFelium(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        if (world.Resources.Special <= 0) return false;
        if (playerStyle) return TrySpendFeliumLikePlayer(world, heroes, log);

        ProductionType? target = null;
        if (UsesHeroes && HasEveryFacility(world))
        {
            if (heroes.GetBlockReason(OccupantKind.MeleeHero) == BalanceBlockReason.Population) return false;
            target = heroes.FirstMissingResource(OccupantKind.MeleeHero);
        }
        target ??= LowestNonGold(world);

        if (!world.TryTrade(target.Value)) return false;
        log.Actions.Add($"펠리움→{target.Value}");
        log.FeliumUsed++;
        return true;
    }

    // 빈 슬롯이 있으면 다음 시설 건설비, 없으면 다음 영웅 생성비를 펠리움으로 한 번에 다 채운다(보스 펠리움으로 새 지역 슬롯을 채우던 운영).
    // 다 채울 만큼 없으면 아껴 둔다 - 목재만 받고 석재가 모자라 못 짓는 일이 없게.
    private bool TrySpendFeliumLikePlayer(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        List<ProductionType> trades = null;
        // 건설비가 이미 있으면(일꾼이 없어 아직 안 지은 것) 영웅 쪽으로 넘어간다. 모자란데 펠리움이 부족하면 건설용으로 아껴 둔다.
        if (world.FreeSlots > 0 && NextBuildType(world) is ProductionType buildType)
        {
            trades = ReplayEconomyStrategy.TradesFor(world, world.BuildCost(buildType));
            if (trades.Count > world.Resources.Special) return false;
        }
        // 그다음은 목재·석재 시설 강화비(기록: 펠리움→목재+석재 후 바로 강화).
        if ((trades == null || trades.Count == 0) && HasEveryFacility(world) && LowestCore(world) is ProductionFacility core)
        {
            trades = ReplayEconomyStrategy.TradesFor(world, core.UpgradeCostCopy);
            if (trades.Count > world.Resources.Special) return false;
        }
        if (trades == null || trades.Count == 0)
        {
            if (!UsesHeroes || !HasEveryFacility(world)) return false;
            OccupantKind kind = FirstHeroKind(world, heroes);
            if (heroes.GetBlockReason(kind) == BalanceBlockReason.Population) return false;
            trades = ReplayEconomyStrategy.TradesFor(world, heroes.GetNextCost(kind));
        }

        if (trades.Count == 0 || trades.Count > world.Resources.Special) return false;
        foreach (ProductionType type in trades)
        {
            world.TryTrade(type);
            log.Actions.Add($"펠리움→{type}");
            log.FeliumUsed++;
        }
        return true;
    }

    private static ProductionType LowestNonGold(EconomySimWorld world)
    {
        ProductionType pick = ProductionType.Wood;
        int lowest = int.MaxValue;
        foreach (ProductionType type in world.BuildableTypes)
        {
            if (type == ProductionType.Gold) continue;
            int stock = world.Resources.GetAmount(type);
            if (stock >= lowest) continue;
            lowest = stock;
            pick = type;
        }
        return pick;
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
        // 실제 플레이 방식: 목재·석재 시설을 최대 단계까지 먼저 올리고 영웅은 남는 자원으로(기록: 5일차에 목재·석재 4단계,
        // 새 지역 시설도 하루 이틀 안에). 둘 다 건설·강화·영웅에 다 들어가는 자원이라 수입이 먼저 올라야 나머지가 굴러간다.
        if (playerStyle && HasEveryFacility(world) && heroes.Roster.Count >= MinHeroesBeforeCore && TryUpgradeCore(world, log)) return true;

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

        if (TryMerge(heroes, out BalanceHero merged))
        {
            log.Actions.Add($"합성 → {merged.Data.HeroName}(T{merged.Data.Tier})");
            return true;
        }

        // 실제 플레이 방식: 직업 강화는 날짜에 맞춰 꾸준히, 영웅보다 먼저(기록: 10일차 4, 20일차 8~9, 30일차 11~13 ≈ 일차 × 0.42).
        // 기본 규칙은 영웅을 더 못 뽑을 때만 올려 뒤로 밀린다.
        if (playerStyle && TryClassOnSchedule(heroes, log)) return true;

        if (TryCreateHero(world, heroes, log)) return true;

        // 실제 플레이 방식: 집을 더 못 올려 인구가 꽉 찼으면 높은 티어부터 합성해 인구를 비운다(T1 셋 = 인구 3 → T2 하나 = 인구 2).
        // 기록: 영웅이 인구 80을 쓰는 상태에서 합성으로 T2~T4까지 올렸다. T1만 쌓으면 밤마다 수십 명씩 쓰러진다.
        // 자원이 모자라 못 뽑는 거면 합성해도 소용없다(인구만 비고 또 못 뽑아 묶음이 다 없어질 때까지 합성하게 된다).
        if (playerStyle && PopulationFull(world) && CanAffordAnyHero(heroes) && TryMergeHighest(heroes, out merged))
        {
            log.Actions.Add($"합성(인구) → {merged.Data.HeroName}(T{merged.Data.Tier})");
            return true;
        }

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
    private bool TryCreateHero(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        OccupantKind first = FirstHeroKind(world, heroes);
        OccupantKind second = first == OccupantKind.MeleeHero ? OccupantKind.RangedHero : OccupantKind.MeleeHero;

        if (!world.Citizens.CheckCanUseCitizen())
        {
            // 자원이 모자라 어차피 못 만들 때는 시민을 모집하지 않는다.
            if (!heroes.CanAfford(first) && !heroes.CanAfford(second)) return false;
            // 실제 플레이 방식: 시민 한도가 꽉 찼으면 집을 강화(지역마다 하나까지는 건설)해 한도를 늘리고 바로 모집한다
            // (기록: 이틀마다 집 강화 → 모집, 26일차 시민 170). 기본 규칙은 일꾼 자리를 채울 때만 한도를 늘려 영웅이 인구에 막힌다.
            if (playerStyle && world.Citizens.CurrentCitizen >= world.Citizens.MaxCitizen && !TryRaiseCitizenCap(world, log)) return false;
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

    // 기본: 3명이 모이면 바로 합성. 실제 플레이 방식: 그 종류 영웅을 놓을 칸이 다 찼을 때만(인구가 찼을 때는 TryHeroAction에서), 낮은 티어부터(기록: T1을 많이 둔 채
    // 버티다 배치 칸이 차면 합성 - 시뮬에서도 이른 합성은 영웅 수와 화력을 같이 줄였다). 웨이브를 모르면(전투 끔) 기본대로.
    private bool TryMerge(BalanceHeroWorld heroes, out BalanceHero merged)
    {
        merged = null;
        BalanceWaveOutlook tonight = heroes.Tonight;
        if (!playerStyle || tonight == null) return heroes.TryMergeAny(out merged);

        foreach (OccupantKind kind in new[] { OccupantKind.MeleeHero, OccupantKind.RangedHero })
        {
            int slots = kind == OccupantKind.MeleeHero ? tonight.MeleeSlots : tonight.RangedSlots;
            if (heroes.CountByKind(kind) < slots) continue;
            for (int tier = 1; tier < EconomySimRunner.MaxHeroTier; tier++)
            {
                if (heroes.TryMerge(kind, tier, out merged)) return true;
            }
        }
        return false;
    }

    // 시민 한도까지 다 찼고 놀고 있는 시민도 없다(집 강화도 못 한 뒤).
    private static bool PopulationFull(EconomySimWorld world) =>
        !world.Citizens.CheckCanUseCitizen() && world.Citizens.CurrentCitizen >= world.Citizens.MaxCitizen;

    private static bool CanAffordAnyHero(BalanceHeroWorld heroes) =>
        heroes.CanAfford(OccupantKind.MeleeHero) || heroes.CanAfford(OccupantKind.RangedHero);

    // 보유한 종류 중 목표 단계(일차 × ClassLevelPerDay)보다 낮은 직업을, 낮은 쪽부터 하나 올린다.
    private static bool TryClassOnSchedule(BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        int target = UnityEngine.Mathf.RoundToInt(log.Day * ClassLevelPerDay);
        int melee = heroes.ClassUpgrades.GetLevel(BalanceHero.MeleeHeroType);
        int ranged = heroes.ClassUpgrades.GetLevel(BalanceHero.RangedHeroType);
        int[] order = melee <= ranged
            ? new[] { BalanceHero.MeleeHeroType, BalanceHero.RangedHeroType }
            : new[] { BalanceHero.RangedHeroType, BalanceHero.MeleeHeroType };
        foreach (int heroType in order)
        {
            OccupantKind kind = heroType == BalanceHero.RangedHeroType ? OccupantKind.RangedHero : OccupantKind.MeleeHero;
            if (heroes.CountByKind(kind) == 0 || heroes.ClassUpgrades.GetLevel(heroType) >= target) continue;
            if (!heroes.TryClassUpgrade(heroType)) continue;
            string label = heroType == BalanceHero.RangedHeroType ? RangedLabel : MeleeLabel;
            log.Actions.Add($"{label} 직업 강화 {heroes.ClassUpgrades.GetLevel(heroType)}");
            return true;
        }
        return false;
    }

    // 가장 높은 티어부터, 종류는 상관없이 3명이 모인 묶음 하나. 높은 티어끼리 합치면 인구도 더 비고(T3 셋 = 인구 9 → T4 = 4)
    // T4까지 올라간다(기록: 26일차부터 T4 - 낮은 티어부터 합치면 같은 T3 셋이 모이지 않아 T4가 안 나온다).
    private static bool TryMergeHighest(BalanceHeroWorld heroes, out BalanceHero merged)
    {
        merged = null;
        for (int tier = EconomySimRunner.MaxHeroTier - 1; tier >= 1; tier--)
        {
            if (heroes.TryMerge(OccupantKind.RangedHero, tier, out merged) || heroes.TryMerge(OccupantKind.MeleeHero, tier, out merged)) return true;
        }
        return false;
    }

    // 근접은 탱커(저지)라 지상 적이 나오는 지역마다 막을 만큼만 뽑고, 화력은 원거리로 올린다(실제 플레이: 지역당 근접 4~5명).
    // 오늘 밤 웨이브(스테이지 정보)를 보고 정한다: 공중만 나오는 지역 몫의 근접은 뽑지 않고(그만큼 원거리),
    // 은신·잠행이 나오면 막아야만 맞으니 근접을 원거리 수만큼까지 더 뽑는다. 전투를 끄면 열린 지역 수로 센다.
    // 수는 티어 합으로 센다(T3 하나 = T1 셋) - 명 수로 세면 합성할 때마다 근접이 기준 아래로 떨어져 근접만 계속 뽑는다.
    private static OccupantKind FirstHeroKind(EconomySimWorld world, BalanceHeroWorld heroes)
    {
        BalanceWaveOutlook tonight = heroes.Tonight;
        int groundRegions = tonight != null ? tonight.GroundRegions : world.UnlockedRegionCount;
        int meleeTarget = MeleePerRegion * System.Math.Max(1, groundRegions);
        int melee = heroes.TierSumByKind(OccupantKind.MeleeHero);
        if (tonight != null && tonight.Hidden) meleeTarget = System.Math.Max(meleeTarget, heroes.TierSumByKind(OccupantKind.RangedHero));
        return melee < meleeTarget ? OccupantKind.MeleeHero : OccupantKind.RangedHero;
    }

    // ── 경제 ──

    private bool TryEconomyAction(EconomySimWorld world, EconomySimDayLog log)
    {
        // 실제 플레이 방식: 빈 슬롯부터 채운다(새 지역이 열리면 그날 시설·집을 다 짓고, 일꾼은 그다음에 모집해 채운다).
        // 기본 규칙은 일자리를 다 채운 뒤에만 지어서, 인구에 막히면 새 지역 슬롯이 계속 비어 있다.
        // 집은 지역마다 하나(1지역 시설 5+집, 2지역 목재·석재·철·금·철+집) - 인구가 모자랄 때마다 집을 지으면 슬롯이 집으로 차서 수입이 무너진다.
        if (playerStyle && world.FreeSlots > 0)
        {
            if (HasEveryFacility(world) && HouseUnderCap(world))
            {
                if (world.TryBuildHouse())
                {
                    log.Actions.Add("집 건설");
                    return true;
                }
            }
            else if (TryBuildFacility(world, log)) return true;
        }

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
    private bool TryFillWorkers(EconomySimWorld world, EconomySimDayLog log)
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

    // 실제 플레이 방식은 집을 지역마다 하나만 짓는다(나머지 인구는 집 강화로).
    private bool HouseUnderCap(EconomySimWorld world) => !playerStyle || world.Houses.Count < world.UnlockedRegionCount;

    // 집 건설, 슬롯이 없으면 기존 집 강화로 최대 시민을 늘린다.
    private bool TryRaiseCitizenCap(EconomySimWorld world, EconomySimDayLog log)
    {
        if (HouseUnderCap(world) && world.TryBuildHouse())
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

        ProductionType? pick = NextBuildType(world);
        if (pick == null || !world.TryBuildFacility(pick.Value)) return false;
        log.Actions.Add($"{pick.Value} 건설");
        return true;
    }

    private ProductionType? NextBuildType(EconomySimWorld world)
    {
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
        return pick;
    }

    // 최대 단계가 아닌 목재·석재 시설 중 가장 낮은 것(자원과 상관없이). 없으면 null.
    private static ProductionFacility LowestCore(EconomySimWorld world)
    {
        ProductionFacility pick = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (facility.ProductionType != ProductionType.Wood && facility.ProductionType != ProductionType.Stone) continue;
            if (facility.UpgradeCount >= facility.MaxUpgrade) continue;
            if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
        }
        return pick;
    }

    // 목재·석재 시설 중 최대가 아닌 것을 단계가 낮은 것부터(같으면 남은 양이 적은 쪽 - 둘을 짝으로 올린다).
    private static bool TryUpgradeCore(EconomySimWorld world, EconomySimDayLog log)
    {
        ProductionFacility pick = PickUpgrade(world, type => type == ProductionType.Wood || type == ProductionType.Stone);
        // 목재·석재가 다 최대면 식량 시설(기록: 3·6·7·8일차에 올려 8일차 최대 - 시민 모집·영웅에 식량이 계속 든다).
        if (pick == null && LowestCore(world) == null) pick = PickUpgrade(world, type => type == ProductionType.Food);
        if (pick == null || !EconomySimWorld.TryUpgrade(pick)) return false;
        log.Actions.Add($"{pick.ProductionType} 강화 {pick.UpgradeCount}");
        return true;
    }

    // 그 종류 시설 중 지금 강화할 수 있는 것을 단계가 낮은 것부터(같으면 남은 양이 적은 자원 쪽).
    private static ProductionFacility PickUpgrade(EconomySimWorld world, System.Predicate<ProductionType> types)
    {
        ProductionFacility pick = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (!types(facility.ProductionType)) continue;
            if (facility.UpgradeCount >= facility.MaxUpgrade || !facility.CheckCanUpgrade()) continue;
            if (pick == null || facility.UpgradeCount < pick.UpgradeCount
                || (facility.UpgradeCount == pick.UpgradeCount
                    && world.Resources.GetAmount(facility.ProductionType) < world.Resources.GetAmount(pick.ProductionType))) pick = facility;
        }
        return pick;
    }

    // 강화 단계가 가장 낮은 시설부터 강화한다. 실제 플레이 방식은 같은 단계면 남은 양이 적은 자원 쪽부터(목재·석재를 고르게).
    private bool TryUpgradeFacility(EconomySimWorld world, EconomySimDayLog log)
    {
        ProductionFacility pick = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (facility.UpgradeCount >= facility.MaxUpgrade || !facility.CheckCanUpgrade()) continue;
            if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
            else if (playerStyle && facility.UpgradeCount == pick.UpgradeCount
                && world.Resources.GetAmount(facility.ProductionType) < world.Resources.GetAmount(pick.ProductionType)) pick = facility;
        }

        if (pick == null || !EconomySimWorld.TryUpgrade(pick)) return false;
        log.Actions.Add($"{pick.ProductionType} 강화 {pick.UpgradeCount}");
        return true;
    }
}
