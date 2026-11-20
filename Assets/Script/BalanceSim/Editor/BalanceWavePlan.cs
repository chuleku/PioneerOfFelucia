using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// 웨이브 표 한 줄을 그날 기준으로 풀어 놓은 것(마릿수 배율·스탯 배율 적용 후).
public class BalanceSpawnGroup
{
    public EnemyTable.Data Enemy;
    public EnemyClass Class;
    public EnemyAttribute Attribute;
    public EnemyType Type;
    public int Count;
    public float StartTime;   // 첫 마리가 나오는 시각(초) = 보스 지연 + SpawnTime
    public float Delay;       // 같은 줄 안에서 다음 마리까지 간격(초)
    public float Hp;
    public float Attack;
    public float Defense;
    public float TilesPerSecond;
    public float AttackCycle;   // 공격 한 번 주기(초) = 1/공속 + 공격 모션 길이(BalanceEnemyMotion)
    public float MovingShare;   // 영웅을 때리며 걷는 동안 실제로 움직이는 시간 비율(모션 중엔 멈춘다)
    public string Source;     // "일반" / "증원" / "보스"
}

// 지역 하나의 하룻밤 계획.
public class BalanceNightPlan
{
    public BalanceRegion Region;
    public int LocalStage;     // 이 지역이 열린 뒤 며칠째 밤인지(1부터)
    public int LookupId;       // 웨이브·포탈 표의 ID(10일차 초과는 1001~1005 순환)
    public int PortalCount;
    public bool CornerPortal;  // 보스 밤 1번 지역은 본진에서 가장 먼 포탈 하나만 연다
    public readonly List<BalanceSpawnGroup> Groups = new();
}

// 그날 열린 지역마다 어떤 적이 몇 마리, 어떤 스탯으로, 언제 나오는지 계산한다.
// 스폰 규칙은 WaveSpawner/SpawnerManager, 스탯은 EnemyStatScaling과 같다(옮긴 곳마다 원본을 적어 둔다).
public static class BalanceWavePlan
{
    // DayState가 10일마다 지원 요청을 켜고 그날 밤 다음 지역이 열린다 -> k번째 지역은 10k+1일차 밤부터 스폰.
    private const int RegionUnlockInterval = 10;
    private const int FirstDay = 1;
    // WaveSpawner.SpawnWave: 1번 지역, 지역 일차가 10의 배수인 밤에 5001 줄(보스)을 bossSpawnDelay 뒤에 낸다.
    private const int BossRegion = 1;
    private const int BossInterval = 10;
    private const int BossWaveId = 5001;
    private const string NormalSource = "일반";
    private const string ReinforceSource = "증원";
    private const string BossSource = "보스";

    // EnemyStatScaling의 배율 표는 private라 리플렉션으로 그대로 읽는다(숫자를 베껴 두지 않는다).
    // 계산식은 SpawnerManager의 정적 상태(해금 지역 수)를 직접 읽어 에디터에선 항상 1배가 되므로 여기서 옮겨 쓴다.
    private static readonly float[] HpTable = ScalingTable("RegionHpScaleTable");
    private static readonly float[] BossHpTable = ScalingTable("RegionBossHpScaleTable");
    private static readonly float[] DefenseTable = ScalingTable("RegionDefenseScaleTable");
    private static readonly float[] BossDefenseTable = ScalingTable("RegionBossDefenseScaleTable");
    private static readonly float[] AttackTable = ScalingTable("RegionAttackScaleTable");
    private static readonly float[] BossAttackTable = ScalingTable("RegionBossAttackScaleTable");
    private static readonly float[] BossHpStep = ScalingTable("BossHpStepScale");
    private static readonly float[] BossDefStep = ScalingTable("BossDefStepScale");
    private static readonly float[] BossAttackStep = ScalingTable("BossAttackStepScale");
    private static readonly int BossStepRounds = ScalingConstant("BossStatStepRounds");
    private static readonly int BossDeepenDay = ScalingConstant("BossStatDeepenDay");
    private const int StatStepDays = 5; // EnemyStatScaling.Compute: 공격·방어는 5일마다 한 단계

    public static int UnlockDay(BalanceRegion region) => FirstDay + region.UnlockOrder * RegionUnlockInterval;

    public static int UnlockedRegionCount(BalanceBattleAssets assets, int day)
    {
        int count = 0;
        foreach (BalanceRegion region in assets.Regions)
        {
            if (UnlockDay(region) <= day) count++;
        }
        return count;
    }

    // 그날 열린 지역마다 계획을 만든다.
    public static List<BalanceNightPlan> Build(BalanceBattleAssets assets, int day)
    {
        int unlocked = UnlockedRegionCount(assets, day);
        int roundsSinceFullUnlock = RoundsSinceFullUnlock(assets, day, unlocked);
        var plans = new List<BalanceNightPlan>();
        foreach (BalanceRegion region in assets.Regions)
        {
            if (UnlockDay(region) > day) continue;
            plans.Add(BuildRegion(assets, region, day, unlocked, roundsSinceFullUnlock));
        }
        return plans;
    }

    // SpawnerManager.SpawnWave -> WaveSpawner.SpawnWave(region, 지역 일차, 해금 수, 전역 일차)와 같은 구성.
    private static BalanceNightPlan BuildRegion(BalanceBattleAssets assets, BalanceRegion region, int day,
        int unlocked, int roundsSinceFullUnlock)
    {
        int localStage = day - UnlockDay(region) + FirstDay;
        int lookupId = WaveSpawner.GetStageLookupId(localStage);
        bool bossNight = region.Region == BossRegion && localStage >= BossInterval && localStage % BossInterval == 0;
        var plan = new BalanceNightPlan
        {
            Region = region,
            LocalStage = localStage,
            LookupId = lookupId,
            CornerPortal = bossNight,
            PortalCount = bossNight ? 1 : assets.Portals.GetCount(region.Region, lookupId, 1),
        };

        // 일반 줄: 마릿수는 전역 일차로 배율(GetScaleCount).
        foreach (WaveTable.Data wave in assets.Waves.GetWave(region.Region, lookupId))
        {
            AddGroup(assets, plan, wave, WaveSpawner.GetScaleCount(wave.Count, day), 0f, day, unlocked, roundsSinceFullUnlock, NormalSource);
        }
        // 증원 줄: 해금 지역 수 - 1 단계만큼, 표에 적힌 마릿수 그대로.
        for (int tier = 0; tier < WaveSpawner.ReinforceTiers(unlocked); tier++)
        {
            foreach (WaveTable.Data wave in assets.Waves.GetWave(region.Region, WaveSpawner.ReinforceBaseId + tier))
            {
                AddGroup(assets, plan, wave, wave.Count, 0f, day, unlocked, roundsSinceFullUnlock, ReinforceSource);
            }
        }
        if (!bossNight) return plan;
        foreach (WaveTable.Data wave in assets.Waves.GetWave(BossRegion, BossWaveId))
        {
            AddGroup(assets, plan, wave, wave.Count, region.BossSpawnDelay, day, unlocked, roundsSinceFullUnlock, BossSource);
        }
        return plan;
    }

    private static void AddGroup(BalanceBattleAssets assets, BalanceNightPlan plan, WaveTable.Data wave, int count,
        float startDelay, int day, int unlocked, int roundsSinceFullUnlock, string source)
    {
        EnemyTable.Data enemy = assets.Enemies.Get(wave.MonsterName);
        if (enemy == null || count <= 0) return;

        EnemyClass enemyClass = EnemyStatScaling.ParseClass(enemy.Class);
        int fiveDayStep = day / StatStepDays;
        plan.Groups.Add(new BalanceSpawnGroup
        {
            Enemy = enemy,
            Class = enemyClass,
            Attribute = ParseAttribute(enemy.Attribute),
            Type = Enum.TryParse(enemy.Type?.Trim(), true, out EnemyType type) ? type : EnemyType.Melee,
            Count = count,
            StartTime = startDelay + wave.SpawnTime,
            Delay = wave.Delay,
            // EnemyStatScaling.Compute와 같은 식. 해금 지역 수만 시뮬 값으로 넣는다.
            Hp = (enemy.Health + day * enemy.UpHealthScale)
                * (RegionScale(HpTable, BossHpTable, enemyClass, unlocked) + BossBonus(enemyClass, day, roundsSinceFullUnlock, BossHpStep)),
            Attack = (enemy.Attack + enemy.UpAttackScale * fiveDayStep)
                * (RegionScale(AttackTable, BossAttackTable, enemyClass, unlocked) + BossBonus(enemyClass, day, roundsSinceFullUnlock, BossAttackStep)),
            Defense = (enemy.Defense + enemy.UpDefenseScale * fiveDayStep)
                * (RegionScale(DefenseTable, BossDefenseTable, enemyClass, unlocked) + BossBonus(enemyClass, day, roundsSinceFullUnlock, BossDefStep)),
            TilesPerSecond = enemy.MoveSpeed / plan.Region.CellSize,
            AttackCycle = BalanceEnemyMotion.AttackCycle(wave.Prefab, enemy.AttackSpeed),
            MovingShare = BalanceEnemyMotion.MovingShare(wave.Prefab, enemy.AttackSpeed),
            Source = source,
        });
    }

    // EnemyStatScaling.RegionScale: 해금 수를 표 길이로 자른 칸(0 이하면 0번 칸).
    private static float RegionScale(float[] normalTable, float[] bossTable, EnemyClass enemyClass, int unlocked)
    {
        float[] table = enemyClass == EnemyClass.Boss ? bossTable : normalTable;
        if (unlocked <= 0) return table[0];
        return table[Mathf.Min(unlocked, table.Length - 1)];
    }

    // EnemyStatScaling.BossFullUnlockBonus: 전 지역 해금 뒤 10라운드마다 한 단계, 100일차 경계 뒤로는 큰 계수.
    private static float BossBonus(EnemyClass enemyClass, int day, int rounds, float[] stepScale)
    {
        if (enemyClass != EnemyClass.Boss) return 0f;
        int steps = rounds / BossStepRounds;
        if (steps <= 0) return 0f;

        int fullUnlockDay = day - rounds;
        int earlyRounds = Mathf.Clamp(BossDeepenDay - fullUnlockDay, 0, rounds);
        int earlySteps = earlyRounds / BossStepRounds;
        int lateSteps = steps - earlySteps;
        return earlySteps * stepScale[0] + lateSteps * stepScale[1];
    }

    // SpawnerManager.RoundsSinceFullUnlock: 전 지역이 열린 뒤 지난 일수(아직 다 안 열렸으면 0).
    private static int RoundsSinceFullUnlock(BalanceBattleAssets assets, int day, int unlocked)
    {
        if (unlocked < assets.Regions.Count) return 0;
        int fullUnlockDay = UnlockDay(assets.Regions[assets.Regions.Count - 1]);
        return Mathf.Max(0, day - fullUnlockDay);
    }

    // EnemyBase.ParseAttribute(internal이라 에디터 어셈블리에서 못 부른다)와 같은 규칙: '|' 또는 ';'로 나눈 플래그 합.
    public static EnemyAttribute ParseAttribute(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return EnemyAttribute.None;
        EnemyAttribute result = EnemyAttribute.None;
        foreach (string token in raw.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse(token.Trim(), true, out EnemyAttribute flag)) result |= flag;
        }
        return result;
    }

    private static float[] ScalingTable(string fieldName) =>
        (float[])typeof(EnemyStatScaling).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static int ScalingConstant(string fieldName) =>
        (int)typeof(EnemyStatScaling).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
}
