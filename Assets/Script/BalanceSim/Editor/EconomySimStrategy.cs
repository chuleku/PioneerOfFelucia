using System.Collections.Generic;

// 낮 동안 무엇을 할지 정하는 봇. 한 판 동안 같은 인스턴스를 쓰고, 행동은 log.Actions에 남긴다.
public interface IEconomySimStrategy
{
    string Name { get; }
    void PlayDay(EconomySimWorld world, EconomySimDayLog log);
}

// 규칙형 봇. 가중치가 높은 자원 시설을 더 많이 짓고, 빈 일자리 -> 시민 모집 -> 집 순서로 인력을 채운다.
// 영웅은 아직 모델에 없으므로 heroReserve만큼 시민을 일터에 넣지 않고 남겨 둔다.
public class RuleEconomyStrategy : IEconomySimStrategy
{
    private const int MaxActionsPerDay = 500; // 규칙이 맞물려 끝나지 않는 경우를 막는 안전장치

    private readonly Dictionary<ProductionType, int> buildWeights;
    private readonly bool upgradeBeforeBuild;
    private readonly int heroReserve;

    public string Name { get; }

    public RuleEconomyStrategy(string name, Dictionary<ProductionType, int> buildWeights, bool upgradeBeforeBuild, int heroReserve)
    {
        Name = name;
        this.buildWeights = buildWeights;
        this.upgradeBeforeBuild = upgradeBeforeBuild;
        this.heroReserve = heroReserve;
    }

    // 미리 정해둔 비교용 봇들.
    public static List<IEconomySimStrategy> CreatePresets(int heroReserve)
    {
        return new List<IEconomySimStrategy>
        {
            new RuleEconomyStrategy("균형", Weights(1, 1, 1, 1, 1), false, heroReserve),
            new RuleEconomyStrategy("건설자원 우선", Weights(2, 1, 1, 1, 2), false, heroReserve),
            new RuleEconomyStrategy("강화 우선", Weights(1, 1, 1, 1, 1), true, heroReserve),
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

    // 할 수 있는 행동이 없을 때까지 한 가지씩 반복한다.
    public void PlayDay(EconomySimWorld world, EconomySimDayLog log)
    {
        for (int step = 0; step < MaxActionsPerDay; step++)
        {
            if (!TryOneAction(world, log)) return;
        }
    }

    private bool TryOneAction(EconomySimWorld world, EconomySimDayLog log)
    {
        if (world.OpenWorkerSlots > 0 && TryFillWorkers(world, log)) return true;

        if (world.OpenWorkerSlots == 0)
        {
            // 새 시설을 지어도 채울 시민 한도가 없으면 집부터 - 안 그러면 슬롯을 시설로 다 채워 집을 못 짓는다.
            if (LacksCitizenRoom(world) && TryRaiseCitizenCap(world, log)) return true;
            if (upgradeBeforeBuild && TryUpgradeFacility(world, log)) return true;
            if (TryBuildFacility(world, log)) return true;
        }

        return TryUpgradeFacility(world, log);
    }

    // 놀고 있는 시민 배치 -> 모자라면 모집 -> 최대 시민이 꽉 찼으면 집 건설/강화.
    private bool TryFillWorkers(EconomySimWorld world, EconomySimDayLog log)
    {
        int idle = world.Citizens.CanUseCitizen - heroReserve;
        if (idle > 0)
        {
            int assigned = AssignIdle(world, idle);
            if (assigned > 0)
            {
                log.Actions.Add($"배치 {assigned}");
                return true;
            }
        }

        int needed = world.OpenWorkerSlots - System.Math.Max(idle, 0);
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

    // 놀고 있는 시민 + 더 모집할 수 있는 수가 새 시설 하나의 일자리보다 적은지.
    private bool LacksCitizenRoom(EconomySimWorld world)
    {
        int idle = System.Math.Max(world.Citizens.CanUseCitizen - heroReserve, 0);
        int recruitable = world.Citizens.MaxCitizen - world.Citizens.CurrentCitizen;
        return idle + recruitable < world.NewFacilityWorkers;
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
