using System.Collections.Generic;

// 실제 플레이 기록(날마다 한 행동)을 그대로 따라 하는 봇. 기록이 끝난 날부터는 규칙형 봇(균형)이 이어 받는다.
// 기록과 같은 운영으로도 시뮬 전투가 보스를 못 잡으면 전투 모델(스킬 미반영)이 모자란 것이고,
// 잡으면 규칙형 봇의 운영이 실제보다 약한 것이다 - 둘을 가르는 기준점으로 쓴다.
// 행동이 실패하면 다른 행동으로 바꾸지 않고 "✗ 행동(이유)"로 남긴다. 기록은 종류별로 묶어 적은 것이라
// 순서가 실제와 다를 수 있어, 실패한 행동은 그날 마지막에 한 번 더 시도한다.
public class ReplayEconomyStrategy : IEconomySimStrategy
{
    private const string MissMark = "✗ ";
    private const string ExtraMark = "+ ";      // 기록 밖에서 남은 펠리움으로 더 한 행동
    private const int MaxExtraActions = 100;
    private const string MeleeLabel = "근접";
    private const string RangedLabel = "원거리";
    // 1일차 기록 "영웅 생성및배치+2"는 종류가 적혀 있지 않다. 2일차에 근접 강화를 해서 근접으로 본다.
    private const OccupantKind Day1HeroKind = OccupantKind.MeleeHero;

    // 행동 하나. 성공하면 기록할 문구, 실패하면 null과 이유를 돌려준다.
    private delegate string ReplayAction(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log, out string failReason);

    private class ReplayStep
    {
        public string Label;
        public ReplayAction Run;
        public bool UsesFelium;
        public bool Optional;   // "못 막을 것 같으면" 같은 선택 행동 - 못 해도 ✗로 세지 않는다
    }

    private readonly Dictionary<int, List<ReplayStep>> script;
    private readonly IEconomySimStrategy fallback;

    public string Name { get; }
    public bool UsesHeroes => true;

    private ReplayEconomyStrategy(string name, Dictionary<int, List<ReplayStep>> script, IEconomySimStrategy fallback)
    {
        Name = name;
        this.script = script;
        this.fallback = fallback;
    }

    public void PlayDay(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log)
    {
        if (!script.TryGetValue(log.Day, out List<ReplayStep> steps))
        {
            fallback.PlayDay(world, heroes, log);
            return;
        }

        var done = new List<string>();
        var failed = new List<(ReplayStep Step, string Reason)>();
        foreach (ReplayStep step in steps)
        {
            string result = step.Run(world, heroes, log, out string reason);
            if (result != null) done.Add(result);
            else failed.Add((step, reason));
        }

        var misses = new List<string>();
        foreach (var (step, _) in failed)
        {
            string result = step.Run(world, heroes, log, out string reason);
            if (result != null) done.Add(result);
            else if (!step.Optional) misses.Add($"{MissMark}{step.Label}({reason})");
        }

        // 실제 플레이는 기록대로 하고도 펠리움이 남았다(10일차 끝 5개) - 남은 펠리움은 영웅 소환에 더 쓴다.
        var extra = new List<string>();
        SpendFeliumOnHeroes(world, heroes, log, extra, FeliumReservedAfter(log.Day));

        log.ReplayMisses += misses.Count;
        log.Actions.AddRange(Compress(done));
        log.Actions.AddRange(Compress(misses));
        log.Actions.AddRange(Compress(extra));
    }

    // 오늘 뒤 기록에서 쓸 펠리움 수(3일차 목재·석재 교환 등). 남은 펠리움을 미리 써 버리면 그 교환이 빠진다.
    private int FeliumReservedAfter(int day)
    {
        int reserved = 0;
        foreach (var (scriptDay, steps) in script)
        {
            if (scriptDay <= day) continue;
            foreach (ReplayStep step in steps)
            {
                if (step.UsesFelium) reserved++;
            }
        }
        return reserved;
    }

    // 남는 펠리움(기록에서 쓸 몫을 뺀 것)으로 영웅을 한 명씩 더 소환한다. 적은 쪽 종류 먼저.
    // 그 영웅의 모자란 자원을 전부 채울 만큼 펠리움이 있을 때만 교환한다 - 목재만 받고 석재가 모자라 못 만드는 일이 없게.
    // 펠리움 없이도 만들 수 있으면 기록에 없는 소환이라 하지 않는다. 인구에 막히면 멈춘다(펠리움은 다음 날로 남는다).
    private static void SpendFeliumOnHeroes(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log,
        List<string> extra, int reserved)
    {
        for (int step = 0; step < MaxExtraActions; step++)
        {
            int spare = world.Resources.Special - reserved;
            if (spare <= 0 || !world.Citizens.CheckCanUseCitizen()) return;

            OccupantKind first = heroes.CountByKind(OccupantKind.MeleeHero) <= heroes.CountByKind(OccupantKind.RangedHero)
                ? OccupantKind.MeleeHero
                : OccupantKind.RangedHero;
            OccupantKind second = first == OccupantKind.MeleeHero ? OccupantKind.RangedHero : OccupantKind.MeleeHero;
            if (!TryFeliumHero(world, heroes, log, extra, first, spare) && !TryFeliumHero(world, heroes, log, extra, second, spare)) return;
        }
    }

    private static bool TryFeliumHero(EconomySimWorld world, BalanceHeroWorld heroes, EconomySimDayLog log,
        List<string> extra, OccupantKind kind, int spare)
    {
        List<ProductionType> trades = TradesFor(world, heroes.GetNextCost(kind));
        if (trades.Count == 0 || trades.Count > spare) return false;

        foreach (ProductionType type in trades)
        {
            world.TryTrade(type);
            log.FeliumUsed++;
            extra.Add($"{ExtraMark}펠리움→{type}");
        }
        if (!heroes.TryCreate(kind, out BalanceHero created)) return false;
        extra.Add($"{ExtraMark}{KindLabel(kind)} 생성 → {created.Data.HeroName}(T{created.Data.Tier})");
        return true;
    }

    // 비용(음수)에서 모자란 자원을 채우는 데 필요한 교환 목록(자원마다 모자란 양 ÷ 교환량 올림).
    internal static List<ProductionType> TradesFor(EconomySimWorld world, (ProductionType Type, int Amount)[] cost)
    {
        var trades = new List<ProductionType>();
        foreach (var (type, amount) in cost)
        {
            int deficit = -amount - world.Resources.GetAmount(type);
            for (int count = 0; count * world.Resources.TradeAmount < deficit; count++) trades.Add(type);
        }
        return trades;
    }

    // 모자란 자원을 펠리움으로 전부 채운다. 펠리움이 모자라면 하나도 바꾸지 않고 null.
    // 채운 게 있으면 " (펠리움→Wood·Stone)", 없으면 빈 문자열을 돌려준다.
    private static string Fund(EconomySimWorld world, (ProductionType Type, int Amount)[] cost, EconomySimDayLog log)
    {
        List<ProductionType> trades = TradesFor(world, cost);
        if (trades.Count > world.Resources.Special) return null;
        if (trades.Count == 0) return string.Empty;

        foreach (ProductionType type in trades)
        {
            world.TryTrade(type);
            log.FeliumUsed++;
        }
        return $" (펠리움→{string.Join("·", trades)})";
    }

    // 같은 문구가 이어지면 "문구 ×n"으로 줄인다(시민 배치 20번 등).
    private static IEnumerable<string> Compress(List<string> lines)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (string line in lines)
        {
            if (!counts.ContainsKey(line)) order.Add(line);
            counts.TryGetValue(line, out int count);
            counts[line] = count + 1;
        }
        foreach (string line in order)
        {
            yield return counts[line] > 1 ? $"{line} ×{counts[line]}" : line;
        }
    }

    // ── 기록 ──

    // 1~12일차 실제 플레이(10일차 보스 처치까지 확인, 11~12일차는 2지역을 여는 운영).
    public static ReplayEconomyStrategy CreateUserPlay(IEconomySimStrategy fallback)
    {
        const ProductionType Wood = ProductionType.Wood;
        const ProductionType Stone = ProductionType.Stone;
        const ProductionType Iron = ProductionType.Iron;
        const ProductionType Gold = ProductionType.Gold;
        const ProductionType Food = ProductionType.Food;
        const OccupantKind Melee = OccupantKind.MeleeHero;
        const OccupantKind Ranged = OccupantKind.RangedHero;
        const int FirstTier = 1;
        const int SecondTier = 2;

        var script = new Dictionary<int, List<ReplayStep>>
        {
            [1] = Day(Build(Wood), Build(Stone), Build(Iron), Build(Gold), Build(Food), BuildHouse(),
                Upgrade(Wood), Upgrade(Stone), Upgrade(Iron), Recruit(20),
                Assign(Wood, 4), Assign(Stone, 4), Assign(Iron, 4), Assign(Gold, 4), Assign(Food, 4),
                Create(Day1HeroKind, 2)),
            [2] = Day(Upgrade(Wood), Upgrade(Stone), Assign(Wood, 1), Assign(Stone, 1), ClassUp(Melee, 1)),
            [3] = Day(Trade(Wood), Trade(Stone), Upgrade(Stone), Upgrade(Wood), Upgrade(Iron), Assign(Iron, 1),
                Create(Ranged, 2), Create(Melee, 1)),
            [4] = Day(Create(Ranged, 2), Merge(Ranged, FirstTier, 1), ClassUp(Ranged, 2), ClassUp(Melee, 1)),
            [5] = Day(Upgrade(Wood), Upgrade(Stone), UpgradeHouse(), Recruit(10), Assign(Wood, 1), Assign(Stone, 1),
                Create(Melee, 1)),
            [6] = Day(Create(Ranged, 3), Merge(Ranged, FirstTier, 1), Create(Melee, 1)),
            [7] = Day(Upgrade(Iron), Upgrade(Gold), Create(Melee, 2), Trade(Iron), ClassUp(Melee, 3), ClassUp(Ranged, 1)),
            [8] = Day(Upgrade(Iron), Upgrade(Gold), UpgradeHouse(), Recruit(10), Assign(Iron, 1), Assign(Gold, 1),
                Create(Melee, 1)),
            [9] = Day(Create(Ranged, 4), Merge(Ranged, FirstTier, 1), Merge(Ranged, SecondTier, 1), Trade(Gold), ClassUp(Ranged, 1)),
            [10] = Day(Create(Melee, 4), Merge(Melee, FirstTier, 3), ClassUp(Ranged, 2)),
            // 2지역이 열리는 날: 원거리 5명(+합성 1회)으로 막고, 보스 펠리움으로 2지역 슬롯에 자원 시설(식량 빼고 철 2개)과 집을 지은 뒤 강화 위주.
            // 시민 모집·일꾼 채우기·강화 대상은 기록에 따로 없어 할 수 있는 만큼 한다(12일차 화면 시민 80/80).
            // 합성 단계는 기록에 없어 T1으로 본다(새로 뽑은 원거리가 대부분 T1).
            [11] = Day(Create(Ranged, 5), Merge(Ranged, FirstTier, 1), BuildFunded(Wood), BuildFunded(Stone), BuildFunded(Iron),
                BuildFunded(Gold), BuildFunded(Iron), BuildHouseFunded(), RecruitAll(), FillWorkers(), UpgradeAll(Food)),
            // "못 막을 것 같으면 원거리 한두 명 더"는 자원이 되면 하는 선택 행동으로 둔다.
            [12] = Day(RecruitAll(), Create(Melee, 4), Create(Ranged, 1), Optional(Create(Ranged, 2))),
        };
        return new ReplayEconomyStrategy("실플레이 재현(1~12일)", script, fallback);
    }

    private static List<ReplayStep> Day(params IEnumerable<ReplayStep>[] groups)
    {
        var steps = new List<ReplayStep>();
        foreach (IEnumerable<ReplayStep> group in groups) steps.AddRange(group);
        return steps;
    }

    private static IEnumerable<ReplayStep> Repeat(int count, string label, ReplayAction run)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new ReplayStep { Label = label, Run = run };
        }
    }

    private static IEnumerable<ReplayStep> Optional(IEnumerable<ReplayStep> steps)
    {
        foreach (ReplayStep step in steps)
        {
            step.Optional = true;
            yield return step;
        }
    }

    private static string KindLabel(OccupantKind kind) => kind == OccupantKind.RangedHero ? RangedLabel : MeleeLabel;

    // ── 경제 행동 ──

    private static IEnumerable<ReplayStep> Build(ProductionType type) =>
        Repeat(1, $"{type} 건설", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.FreeSlots <= 0 ? "빈 슬롯 없음" : "자원 부족";
            return world.TryBuildFacility(type) ? $"{type} 건설" : null;
        });

    private static IEnumerable<ReplayStep> BuildHouse() =>
        Repeat(1, "집 건설", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.FreeSlots <= 0 ? "빈 슬롯 없음" : "자원 부족";
            return world.TryBuildHouse() ? "집 건설" : null;
        });

    // 모자란 건설 자원을 펠리움으로 채워 짓는다(보스 처치 보상으로 새 지역 슬롯을 채울 때).
    private static IEnumerable<ReplayStep> BuildFunded(ProductionType type) =>
        Repeat(1, $"{type} 건설", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog log, out string reason) =>
        {
            reason = world.FreeSlots <= 0 ? "빈 슬롯 없음" : "펠리움 부족";
            if (world.FreeSlots <= 0) return null;
            string funded = Fund(world, world.BuildCost(type), log);
            return funded != null && world.TryBuildFacility(type) ? $"{type} 건설{funded}" : null;
        });

    private static IEnumerable<ReplayStep> BuildHouseFunded() =>
        Repeat(1, "집 건설", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog log, out string reason) =>
        {
            reason = world.FreeSlots <= 0 ? "빈 슬롯 없음" : "펠리움 부족";
            if (world.FreeSlots <= 0) return null;
            string funded = Fund(world, world.HouseCost(), log);
            return funded != null && world.TryBuildHouse() ? $"집 건설{funded}" : null;
        });

    // 강화 단계가 가장 낮은 시설부터 자원이 되는 만큼 강화한다(excluded 종류는 빼고). 기록에 대상이 없을 때 쓴다.
    private static IEnumerable<ReplayStep> UpgradeAll(ProductionType excluded) =>
        Repeat(1, "시설 강화", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = "자원 부족";
            int count = 0;
            for (; count < MaxExtraActions; count++)
            {
                ProductionFacility pick = null;
                foreach (ProductionFacility facility in world.Facilities)
                {
                    if (facility.ProductionType == excluded || !facility.CheckCanUpgrade()) continue;
                    if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
                }
                if (pick == null || !EconomySimWorld.TryUpgrade(pick)) break;
            }
            return count > 0 ? $"시설 강화 ×{count}" : null;
        });

    // 그 종류 시설 중 강화 단계가 가장 낮은 것.
    private static IEnumerable<ReplayStep> Upgrade(ProductionType type) =>
        Repeat(1, $"{type} 강화", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            ProductionFacility pick = null;
            foreach (ProductionFacility facility in world.Facilities)
            {
                if (facility.ProductionType != type || facility.UpgradeCount >= facility.MaxUpgrade) continue;
                if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
            }
            reason = pick == null ? "강화할 시설 없음" : "자원 부족";
            return pick != null && EconomySimWorld.TryUpgrade(pick) ? $"{type} 강화 {pick.UpgradeCount}" : null;
        });

    private static IEnumerable<ReplayStep> UpgradeHouse() =>
        Repeat(1, "집 강화", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Houses.Count == 0 ? "집 없음" : "자원 부족";
            foreach (House house in world.Houses)
            {
                if (EconomySimWorld.TryUpgrade(house)) return $"집 강화 {house.UpgradeCount}";
            }
            return null;
        });

    // 한 명씩 모집한다 - 일부만 되면 된 만큼 남고 나머지는 실패로 남는다.
    private static IEnumerable<ReplayStep> Recruit(int count) =>
        Repeat(count, "시민 모집", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Citizens.CurrentCitizen >= world.Citizens.MaxCitizen ? "최대 시민" : "식량 부족";
            return world.Recruit(1) == 1 ? "시민 모집" : null;
        });

    // 인구 한도와 식량이 허락하는 만큼 한 번에 모집한다.
    private static IEnumerable<ReplayStep> RecruitAll() =>
        Repeat(1, "시민 모집", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Citizens.CurrentCitizen >= world.Citizens.MaxCitizen ? "최대 시민" : "식량 부족";
            int recruited = world.Recruit(int.MaxValue);
            return recruited > 0 ? $"시민 모집 ×{recruited}" : null;
        });

    // 놀고 있는 시민으로 빈 일자리를 채운다(새로 지은 시설 일꾼).
    private static IEnumerable<ReplayStep> FillWorkers() =>
        Repeat(1, "일꾼 채우기", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Citizens.CanUseCitizen <= 0 ? "놀고 있는 시민 없음" : "빈 일자리 없음";
            int assigned = 0;
            foreach (ProductionFacility facility in world.Facilities)
            {
                int open = facility.MaxWorker - facility.WorkerAmount;
                if (open > 0) assigned += EconomySimWorld.AssignWorkers(facility, open);
            }
            return assigned > 0 ? $"일꾼 배치 ×{assigned}" : null;
        });

    private static IEnumerable<ReplayStep> Assign(ProductionType type, int count) =>
        Repeat(count, $"{type} 일꾼 배치", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Citizens.CanUseCitizen <= 0 ? "놀고 있는 시민 없음" : "빈 일자리 없음";
            if (world.Citizens.CanUseCitizen <= 0) return null;
            foreach (ProductionFacility facility in world.Facilities)
            {
                if (facility.ProductionType != type) continue;
                if (EconomySimWorld.AssignWorkers(facility, 1) == 1) return $"{type} 일꾼 배치";
            }
            return null;
        });

    private static IEnumerable<ReplayStep> Trade(ProductionType type)
    {
        yield return new ReplayStep
        {
            Label = $"펠리움→{type}",
            UsesFelium = true,
            Run = (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog log, out string reason) =>
            {
                reason = "펠리움 없음";
                if (!world.TryTrade(type)) return null;
                log.FeliumUsed++;
                return $"펠리움→{type}";
            },
        };
    }

    // ── 영웅 행동 ──

    private static IEnumerable<ReplayStep> Create(OccupantKind kind, int count) =>
        Repeat(count, $"{KindLabel(kind)} 생성", (EconomySimWorld _, BalanceHeroWorld heroes, EconomySimDayLog _, out string reason) =>
        {
            reason = heroes.GetBlockReason(kind) ?? "뽑기 실패";
            return heroes.TryCreate(kind, out BalanceHero created)
                ? $"{KindLabel(kind)} 생성 → {created.Data.HeroName}(T{created.Data.Tier})"
                : null;
        });

    private static IEnumerable<ReplayStep> Merge(OccupantKind kind, int tier, int count) =>
        Repeat(count, $"{KindLabel(kind)} T{tier} 합성", (EconomySimWorld _, BalanceHeroWorld heroes, EconomySimDayLog _, out string reason) =>
        {
            reason = "같은 영웅 3명 없음";
            return heroes.TryMerge(kind, tier, out BalanceHero merged)
                ? $"{KindLabel(kind)} 합성 → {merged.Data.HeroName}(T{merged.Data.Tier})"
                : null;
        });

    private static IEnumerable<ReplayStep> ClassUp(OccupantKind kind, int count)
    {
        int heroType = kind == OccupantKind.RangedHero ? BalanceHero.RangedHeroType : BalanceHero.MeleeHeroType;
        return Repeat(count, $"{KindLabel(kind)} 직업 강화", (EconomySimWorld _, BalanceHeroWorld heroes, EconomySimDayLog _, out string reason) =>
        {
            reason = heroes.ClassUpgrades.CanLevelUp(heroType) ? "자원 부족" : "최대 단계";
            return heroes.TryClassUpgrade(heroType)
                ? $"{KindLabel(kind)} 직업 강화 {heroes.ClassUpgrades.GetLevel(heroType)}"
                : null;
        });
    }
}
