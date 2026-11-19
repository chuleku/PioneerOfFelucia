using System;
using System.Collections.Generic;
using UnityEngine;

// 하루치 기록. 자원 배열은 ProductionType 값을 인덱스로 쓴다.
public class EconomySimDayLog
{
    public int Day;
    public int UnlockedRegions;
    public readonly int[] Spent = new int[EconomySimRunner.ResourceTypeCount];  // 낮 동안 쓴 양(양수)
    public readonly int[] Income = new int[EconomySimRunner.ResourceTypeCount]; // 밤이 끝나고 받은 양
    public readonly int[] DayEnd = new int[EconomySimRunner.ResourceTypeCount]; // 낮이 끝난 시점 보유량(인게임 표시와 같은 기준)
    public readonly int[] End = new int[EconomySimRunner.ResourceTypeCount];    // 지급 후 보유량(다음 날 시작값)
    public readonly int[] FacilityCounts = new int[EconomySimRunner.ResourceTypeCount];
    public int HouseCount;
    public int CurrentCitizen;
    public int MaxCitizen;
    public int AssignedWorkers;
    public int OpenWorkerSlots;
    public int UsedSlots;
    public int TotalSlots;
    public readonly List<string> Actions = new();
}

// 타이틀 강화 계열마다 앞에서부터 몇 단계를 해금한 상태로 볼지.
public class EconomySimTitleInput
{
    public int ConstructDiscount;
    public int UpgradeDiscount;
    public int ProductAmount;
    public int StartingResource;
    public int MaxCitizen;
}

// 낮(봇 행동) -> 밤(전투 없음) -> 생산 지급을 하루 단위로 반복한다.
public static class EconomySimRunner
{
    public static readonly int ResourceTypeCount = Enum.GetValues(typeof(ProductionType)).Length;

    // DayState가 10일마다 지원 요청을 켜고, 그날 밤 결과 화면에서 다음 지역이 열린다 -> 11, 21일차부터 사용 가능.
    private const int RegionUnlockInterval = 10;
    // 맵 모듈 프리팹 중 MapModule_A(1번 지역)만 열린 상태로 시작한다.
    private const int StartingRegions = 1;
    // 0일차는 튜토리얼이 끝나면 자원을 시작값으로 되돌리므로 1일차부터 돈다.
    private const int FirstDay = 1;

    public static List<EconomySimDayLog> Run(EconomySimAssets assets, EconomySimTitleInput title,
        IEconomySimStrategy strategy, int dayCount)
    {
        var logs = new List<EconomySimDayLog>(dayCount);
        using var world = new EconomySimWorld(assets, BuildUpgradeState(assets, title));

        for (int day = FirstDay; day < FirstDay + dayCount; day++)
        {
            var log = new EconomySimDayLog { Day = day };
            world.SetUnlockedRegions(StartingRegions + (day - FirstDay) / RegionUnlockInterval);

            int[] before = Snapshot(world.Resources);
            strategy.PlayDay(world, log);
            int[] afterDay = Snapshot(world.Resources);

            foreach (var (type, amount) in world.PayOut())
            {
                log.Income[(int)type] += amount;
            }

            for (int index = 0; index < ResourceTypeCount; index++)
            {
                log.Spent[index] = before[index] - afterDay[index];
                log.DayEnd[index] = afterDay[index];
            }
            FillState(world, log);
            logs.Add(log);
        }
        return logs;
    }

    // PlayerPrefs의 실제 타이틀 진행도를 쓰지 않도록 생성 직후 해금 목록을 덮어쓴다.
    // Unlock/AddPoints는 PlayerPrefs에 저장해 버리므로 부르지 않는다.
    private static UpgradeState BuildUpgradeState(EconomySimAssets assets, EconomySimTitleInput title)
    {
        var ids = new List<string>();
        AddFirstIds(ids, assets.EconomyConfig.ConstructCostUpgrades, title.ConstructDiscount);
        AddFirstIds(ids, assets.EconomyConfig.UpgradeCostUpgrades, title.UpgradeDiscount);
        AddFirstIds(ids, assets.EconomyConfig.ProductAmountUpgrades, title.ProductAmount);
        AddFirstIds(ids, assets.StartingResourceUpgrades, title.StartingResource);
        AddFirstIds(ids, assets.MaxCitizenUpgrades, title.MaxCitizen);

        var state = new UpgradeState();
        state.Restore(new UpgradeSaveData { unlockedIds = ids });
        return state;
    }

    private static void AddFirstIds(List<string> ids, IReadOnlyList<BaseUpgradeData> branch, int count)
    {
        for (int index = 0; index < Mathf.Min(count, branch.Count); index++)
        {
            if (branch[index] != null) ids.Add(branch[index].id);
        }
    }

    private static int[] Snapshot(ResourcesManager resources)
    {
        var amounts = new int[ResourceTypeCount];
        for (int index = 0; index < ResourceTypeCount; index++)
        {
            amounts[index] = resources.GetAmount((ProductionType)index);
        }
        return amounts;
    }

    private static void FillState(EconomySimWorld world, EconomySimDayLog log)
    {
        int[] end = Snapshot(world.Resources);
        Array.Copy(end, log.End, ResourceTypeCount);
        foreach (ProductionFacility facility in world.Facilities)
        {
            log.FacilityCounts[(int)facility.ProductionType]++;
        }
        log.UnlockedRegions = world.UnlockedRegionCount;
        log.HouseCount = world.Houses.Count;
        log.CurrentCitizen = world.Citizens.CurrentCitizen;
        log.MaxCitizen = world.Citizens.MaxCitizen;
        log.AssignedWorkers = world.AssignedWorkers;
        log.OpenWorkerSlots = world.OpenWorkerSlots;
        log.TotalSlots = world.TotalSlots;
        log.UsedSlots = world.TotalSlots - world.FreeSlots;
    }
}
