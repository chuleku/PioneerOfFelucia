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
    public int HeroCitizens;
    public int OpenWorkerSlots;
    public int UsedSlots;
    public int TotalSlots;
    public readonly List<string> Actions = new();

    // 영웅
    public readonly int[] HeroTierCounts = new int[EconomySimRunner.MaxHeroTier + 1]; // 인덱스 = 티어
    public int MeleeCount;
    public int RangedCount;
    public int CreatedToday;
    public int MeleeClassLevel;
    public int RangedClassLevel;
    public string HeroBlock;    // 낮이 끝났을 때 근접 영웅 하나를 더 못 만든 이유(인구/자원 이름). 영웅을 안 쓰는 전략이면 null
    public bool HasPower;       // 비교표용 실행은 마지막 날만 전투력을 계산한다
    public float PowerDps;
    public float PowerHp;

    // 펠리움(특수 자원)
    public int FeliumGained;   // 이 밤이 끝나며 받은 양(무피해 +1, 보스 +5)
    public int FeliumUsed;     // 낮 동안 교환에 쓴 개수
    public int FeliumEnd;      // 지급 후 보유량

    // 실플레이 재현 봇에서 기록대로 못 한 행동 수(다른 봇은 0)
    public int ReplayMisses;

    // 전투(3단계, 전투를 켰을 때만)
    public bool HasBattle;
    public int BaseHp;            // 밤이 끝난 뒤 본진 체력
    public int EnemySpawned;
    public int EnemyLeaked;
    public int BaseDamage;
    public bool PerfectNight;
    public bool BossKilled;
    public bool GameOver;
    public int Placed;
    public int Inventory;
    public int HeroDeaths;
    public string BattleNote;

    public int HeroCount => MeleeCount + RangedCount;
}

// 타이틀 강화 계열마다 앞에서부터 몇 단계를 해금한 상태로 볼지.
public class EconomySimTitleInput
{
    public int ConstructDiscount;
    public int UpgradeDiscount;
    public int ProductAmount;
    public int StartingResource;
    public int MaxCitizen;
    public int HeroCreateDiscount;
    public int HeroUpgradeDiscount;
    public int BaseHp;
}

// 펠리움 수입. 전투를 켜면 실제 밤 결과(무피해·보스 처치)로 정하고, 끄면 아래 가정값을 쓴다.
public class EconomySimFeliumInput
{
    public bool UseBattle = true;         // 3단계 전투 결과로 펠리움·본진 체력을 정한다
    public int PerfectNightPercent = 100; // 본진 피해 없이 넘긴 밤의 비율(시드별로 날마다 뽑는다)
    public bool KillBoss = true;          // 1번 지역 10일마다 나오는 보스(DeathAttacker)를 잡는다고 볼지
}

// 낮(봇 행동) -> 밤(전투 없음) -> 생산 지급을 하루 단위로 반복한다.
public static class EconomySimRunner
{
    public static readonly int ResourceTypeCount = Enum.GetValues(typeof(ProductionType)).Length;
    public const int MaxHeroTier = 4;

    // 맵 모듈 프리팹 중 MapModule_A(1번 지역)만 열린 상태로 시작한다.
    public const int StartingRegions = 1;
    // DayState가 10일마다 지원 요청을 켜고, 그날 밤 결과 화면에서 다음 지역이 열린다 -> 11, 21일차부터 사용 가능.
    private const int RegionUnlockInterval = 10;
    // 0일차는 튜토리얼이 끝나면 자원을 시작값으로 되돌리므로 1일차부터 돈다.
    private const int FirstDay = 1;
    // EnviromentManager: 무피해(perfactDefence)로 밤을 넘기면 아침에 GetSpecial 1번.
    private const int PerfectNightFelium = 1;
    // EnemyBase.BossSpecialDrop. 보스는 WaveSpawner가 1번 지역 currentStage % 10 == 0인 밤에 낸다.
    private const int BossFelium = 5;
    private const int BossInterval = 10;
    private const int PercentScale = 100;
    private const string NightSeedSuffix = ":night";

    // powerEveryDay가 false면 마지막 날만 전투력을 계산한다(전략 비교용 반복 실행을 가볍게).
    public static List<EconomySimDayLog> Run(EconomySimAssets assets, BalanceHeroAssets heroAssets, EconomySimTitleInput title,
        EconomySimFeliumInput felium, BalanceBattleAssets battle, IEconomySimStrategy strategy, int dayCount, string seed,
        bool powerEveryDay, Dictionary<(HeroData, int), HeroSimResult> powerCache)
    {
        var logs = new List<EconomySimDayLog>(dayCount);
        UpgradeState upgradeState = BuildUpgradeState(assets, heroAssets, battle, title);
        using var world = new EconomySimWorld(assets, upgradeState);
        var heroes = new BalanceHeroWorld(heroAssets, world, upgradeState, seed, powerCache);
        int lastDay = FirstDay + dayCount - 1;
        bool useBattle = felium.UseBattle && battle != null && battle.IsValid;
        // GameManager.Construct: 프리팹 hp + 타이틀 hpUpgrades. 밤마다 깎이고 회복되지 않는다.
        int baseHp = useBattle ? battle.BaseHp + (int)upgradeState.GetTotalEffect(battle.BaseHpUpgrades) : 0;

        for (int day = FirstDay; day <= lastDay; day++)
        {
            var log = new EconomySimDayLog { Day = day };
            world.SetUnlockedRegions(StartingRegions + (day - FirstDay) / RegionUnlockInterval);
            heroes.StartDay();
            heroes.Tonight = useBattle ? BalanceWaveOutlook.For(battle, day) : null;

            int[] before = Snapshot(world.Resources);
            strategy.PlayDay(world, heroes, log);
            int[] afterDay = Snapshot(world.Resources);
            log.HeroBlock = strategy.UsesHeroes ? heroes.GetBlockReason(OccupantKind.MeleeHero) : null;

            foreach (var (type, amount) in world.PayOut())
            {
                log.Income[(int)type] += amount;
            }
            log.FeliumGained = useBattle ? BattleNight(battle, heroes, day, seed, ref baseHp, log) : NightFelium(felium, seed, day);
            world.GrantSpecial(log.FeliumGained);

            for (int index = 0; index < ResourceTypeCount; index++)
            {
                log.Spent[index] = before[index] - afterDay[index];
                log.DayEnd[index] = afterDay[index];
            }
            FillState(world, log);
            FillHeroState(heroes, log, powerEveryDay || day == lastDay || log.GameOver);
            logs.Add(log);
            if (log.GameOver) break;
        }
        return logs;
    }

    // 밤 전투를 돌려 본진 체력을 깎고, 그 결과로 받을 펠리움을 돌려준다(무피해 +1, 보스 처치 +5).
    // NightState.Exit: 밤 시작 때와 본진 체력이 같으면 무피해. 보스가 닿거나 체력이 0이 되면 패배.
    private static int BattleNight(BalanceBattleAssets battle, BalanceHeroWorld heroes, int day, string seed, ref int baseHp, EconomySimDayLog log)
    {
        BalanceNightResult night = BalanceBattle.RunNight(battle, heroes, day, seed);
        baseHp = night.BossLeaked ? 0 : Mathf.Max(0, baseHp - night.BaseDamage);

        log.HasBattle = true;
        log.BaseHp = baseHp;
        log.EnemySpawned = night.Spawned;
        log.EnemyLeaked = night.Leaked;
        log.BaseDamage = night.BaseDamage;
        log.PerfectNight = night.BaseDamage == 0 && !night.BossLeaked;
        log.BossKilled = night.BossKilled;
        log.GameOver = baseHp <= 0;
        log.Placed = night.Placed;
        log.Inventory = night.Inventory;
        log.HeroDeaths = night.HeroDeaths;
        // 자동이면 지역마다 고른 방식이 요약 앞에 붙어 있다.
        string placement = night.Placement == BalancePlacement.Auto ? string.Empty : $"[{BalanceBattle.PlacementName(night.Placement)}] ";
        log.BattleNote = placement + string.Join(" · ", night.RegionNotes) + (night.TimedOut ? " · 시간 초과" : string.Empty)
            + (night.BossNote != null ? $" · {night.BossNote}" : string.Empty);
        return (log.PerfectNight ? PerfectNightFelium : 0) + (night.BossKilled ? BossFelium : 0);
    }

    // 이 밤이 끝나며 받는 펠리움. 무피해 여부는 시드·일차로 정해 같은 입력이면 항상 같은 결과가 나온다.
    private static int NightFelium(EconomySimFeliumInput felium, string seed, int day)
    {
        int gained = 0;
        var rng = new System.Random(GameSeeding.Derive(seed + NightSeedSuffix, day));
        if (rng.Next(PercentScale) < felium.PerfectNightPercent) gained += PerfectNightFelium;
        if (felium.KillBoss && day % BossInterval == 0) gained += BossFelium;
        return gained;
    }

    // PlayerPrefs의 실제 타이틀 진행도를 쓰지 않도록 생성 직후 해금 목록을 덮어쓴다.
    // Unlock/AddPoints는 PlayerPrefs에 저장해 버리므로 부르지 않는다.
    private static UpgradeState BuildUpgradeState(EconomySimAssets assets, BalanceHeroAssets heroAssets, BalanceBattleAssets battle,
        EconomySimTitleInput title)
    {
        var ids = new List<string>();
        AddFirstIds(ids, assets.EconomyConfig.ConstructCostUpgrades, title.ConstructDiscount);
        AddFirstIds(ids, assets.EconomyConfig.UpgradeCostUpgrades, title.UpgradeDiscount);
        AddFirstIds(ids, assets.EconomyConfig.ProductAmountUpgrades, title.ProductAmount);
        AddFirstIds(ids, assets.StartingResourceUpgrades, title.StartingResource);
        AddFirstIds(ids, assets.MaxCitizenUpgrades, title.MaxCitizen);
        AddFirstIds(ids, heroAssets.CreateCostUpgrades, title.HeroCreateDiscount);
        AddFirstIds(ids, heroAssets.ClassConfig.UpgradeCostUpgrades, title.HeroUpgradeDiscount);
        if (battle != null) AddFirstIds(ids, battle.BaseHpUpgrades, title.BaseHp);

        var state = new UpgradeState();
        state.Restore(new UpgradeSaveData { unlockedIds = ids });
        return state;
    }

    private static void AddFirstIds(List<string> ids, IReadOnlyList<BaseUpgradeData> branch, int count)
    {
        for (int index = 0; index < Mathf.Min(count, branch.Count); index++)
        {
            if (branch[index] != null && !ids.Contains(branch[index].id)) ids.Add(branch[index].id);
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
        log.HeroCitizens = world.Citizens.HeroUsedCitizen;
        log.OpenWorkerSlots = world.OpenWorkerSlots;
        log.TotalSlots = world.TotalSlots;
        log.UsedSlots = world.TotalSlots - world.FreeSlots;
        log.FeliumEnd = world.Resources.Special;
    }

    private static void FillHeroState(BalanceHeroWorld heroes, EconomySimDayLog log, bool withPower)
    {
        foreach (BalanceHero hero in heroes.Roster)
        {
            log.HeroTierCounts[Mathf.Clamp(hero.Data.Tier, 0, MaxHeroTier)]++;
        }
        log.MeleeCount = heroes.CountByKind(OccupantKind.MeleeHero);
        log.RangedCount = heroes.CountByKind(OccupantKind.RangedHero);
        log.CreatedToday = heroes.CreatedToday;
        log.MeleeClassLevel = heroes.ClassUpgrades.GetLevel(BalanceHero.MeleeHeroType);
        log.RangedClassLevel = heroes.ClassUpgrades.GetLevel(BalanceHero.RangedHeroType);

        if (!withPower) return;
        (log.PowerDps, log.PowerHp) = heroes.CalculatePower();
        log.HasPower = true;
    }
}
