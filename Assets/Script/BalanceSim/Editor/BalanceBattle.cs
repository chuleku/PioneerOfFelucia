using System.Collections.Generic;
using UnityEngine;

// 하룻밤 전투 결과(지역 전체 합).
public class BalanceNightResult
{
    public int Spawned;
    public int Killed;
    public int Leaked;
    public int BaseDamage;        // 본진 체력 감소량(일반 1, 엘리트 2)
    public bool BossLeaked;       // 보스가 본진에 닿음 = 즉시 패배
    public bool BossSpawned;
    public bool BossKilled;
    public int HeroDeaths;
    public int Placed;            // 필드에 놓인 영웅 수
    public int Inventory;         // 칸이 없어 못 놓은 영웅 수
    public bool TimedOut;         // 제한 시간 안에 안 끝나 남은 적을 새어 나간 것으로 처리했다
    public string BossNote;       // 보스 결과 상세(남은 체력, 막힌 시간, 사거리 안에 있던 영웅 수)
    public BalancePlacement Placement; // 이 밤에 쓴 근접 배치 방식(자동이면 고른 쪽)
    public readonly List<string> RegionNotes = new(); // "지역1 누수 2" 같은 요약
}

// 배치 칸 하나에 선 영웅. 스탯은 HeroSimBuilder 결과(직업 강화 반영)를 쓰고, 공격 모양은 평타 패턴 첫 공격에서 읽는다.
public class BalanceFieldHero
{
    public BalanceHero Hero;
    public HeroSimEntry Entry;
    public List<HeroSimHeavyChance> Heavies;
    public Vector2Int Tile;
    public bool IsMelee;
    public float MaxHp;
    public float Attack;
    public float Defense;
    public float AttackInterval;
    public int BlockCount;
    public int Range;
    public RangeShape Shape;
    public AttackType AttackType;
    public int TargetCount;
    public int AreaRange;
    public int HitsPerAttack;
    public EnemyAttribute Unattackable;
    public EnemyAttribute AreaUnattackable;
    public float Dps;
    public float DamageScale = 1f;   // 시뮬에 없는 스킬 등을 메우는 피해 보정(배수)

    // 전투 중 상태
    public float Hp;
    public float Cooldown;
    public int Blocking;
    public object Target;   // 지금 잡고 있는 적(BalanceBattle.BattleEnemy). 죽거나 사거리를 벗어나면 새로 잡는다
    public bool Alive => Hp > 0f;
    public float SkillAttackScale = 1f;  // 플레이어 공격 버프 장판 안이면 ATK 배율(매 칸 다시 계산)
    public float SkillAttackSpeedAdd;    // 같은 장판의 AS 가산
    public readonly Dictionary<(int Defense, float AttackScale), float> HitByDefense = new(); // 적 방어력·버프 배율별 평균 1타(HeroSimCalc) 캐시
}

// 칸 사이 사거리 판정. TileShapeQuery(맵 코드)와 같은 모양: 마름모=맨해튼, 정사각=체비셰프, 십자=같은 줄.
public static class BalanceRange
{
    public static bool Contains(Vector2Int origin, Vector2Int target, int range, RangeShape shape)
    {
        int dx = Mathf.Abs(target.x - origin.x);
        int dy = Mathf.Abs(target.y - origin.y);
        switch (shape)
        {
            case RangeShape.Square: return dx <= range && dy <= range;
            case RangeShape.Cross: return (dx == 0 && dy <= range) || (dy == 0 && dx <= range);
            default: return dx + dy <= range;
        }
    }

    // EnemyTargeting.Distance와 같은 맨해튼 거리(적의 사거리 판정).
    public static int Manhattan(Vector2Int a, Vector2Int b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
}

// 하룻밤을 지역별로 돌린다: 포탈 고르기 -> 영웅 배치 -> 0.25초 단위로 적 이동·저지·공격·누수.
// 단순화(옮기지 않은 것): 적·영웅 스킬, 오라·버프·디버프, 장판, 힐, 재생·폭주·화염 특성, 체인 공격.
public static partial class BalanceBattle
{
    private const float StepSeconds = 0.25f;
    private const float MaxNightSeconds = 600f;      // 이 시간 안에 안 끝나면 남은 적은 새어 나간 것으로 본다
    // 근접 저지 칸은 레인의 이 비율 지점에 가까울수록 좋게 본다(0 = 포탈 입구, 1 = 본진).
    private const float EntranceLanePreference = 0f;
    private const float CoreLanePreference = 0.66f;
    // 원거리 영웅이 저지 칸을 사거리에 넣으면 칸당 이만큼 더 친다. 입구 집중이면 막힌 적을 같이 때리는 게 핵심이라 크게 준다.
    private const float EntranceBlockCoverageBonus = 10f;
    private const float CoreBlockCoverageBonus = 3f;
    private const int LaneTileScore = 1000;           // 근접 칸 점수: 지나가는 레인 1개 = 이 점수
    // 경로 분산: 다른 근접과 경로상 거리(레인 길이 비율) 가산, 같은 거리면 입구 쪽 가산. 레인 1개 점수보다는 작게.
    private const float SpreadGapScore = 600f;
    private const float SpreadEntranceScore = 100f;
    private const int NormalLeakDamage = 1;           // GameManager.HpDamage
    private const int EliteLeakDamage = 2;
    private const string PortalSeedSuffix = ":portal";
    private const string PathSeedSuffix = ":path";
    private const float PercentScale = 100f;
    // 지역 간 재배치: 한 밤에 최대 몇 명까지 옮겨 보고, 한 번 옮길 때 후보를 몇 명까지 시험할지(밤 하나를 그만큼 더 돌린다).
    private const int MaxRegionMoves = 6;
    private const int MaxMoveTrials = 3;

    // ── 밤 하나 ──

    // 같은 밤을 지역 배분만 바꿔 다시 돌려 볼 때의 한 판 결과.
    private class NightRun
    {
        public BalanceNightResult Total;
        public readonly List<BalanceNightResult> Regions = new();
        public int Unplaced;   // 지역에 배분됐지만 칸이 없어 못 놓은 영웅
    }

    // 영웅을 지역에 나눠(Assign) 밤을 돌린 뒤, 새는 지역과 안 새는 지역이 같이 있으면 안 새는 쪽 영웅을 한 명씩
    // 새는 쪽으로 옮겨 다시 돌려 본다(실제 플레이어가 1지역 영웅을 2지역으로 옮기는 재배치). 나아지지 않으면 멈춘다.
    // 같은 시드라 적 구성·포탈은 그대로고 배치만 다르다.
    public static BalanceNightResult RunNight(BalanceBattleAssets assets, BalanceHeroWorld heroes, int day, string seed)
    {
        List<BalanceNightPlan> plans = BalanceWavePlan.Build(assets, day);
        var activeLanes = new List<List<int>>();
        foreach (BalanceNightPlan plan in plans) activeLanes.Add(PickActiveLanes(plan, seed, day));

        List<BalanceFieldHero> roster = BuildRoster(heroes, assets);
        List<List<BalanceFieldHero>> groups = Assign(roster, plans, out int inventory);
        NightRun best = RunGroups(assets, plans, activeLanes, groups, seed, day);
        int moves = 0;
        for (; moves < MaxRegionMoves; moves++)
        {
            NightRun improved = null;
            foreach (List<List<BalanceFieldHero>> moved in MoveCandidates(best, groups, plans))
            {
                NightRun trial = RunGroups(assets, plans, activeLanes, moved, seed, day);
                if (!IsBetter(trial.Total, best.Total)) continue;
                improved = trial;
                groups = moved;
                break;
            }
            if (improved == null) break;
            best = improved;
        }

        BalanceNightResult result = best.Total;
        result.Inventory = inventory + best.Unplaced;
        result.Placed = roster.Count - result.Inventory;
        if (moves > 0) result.RegionNotes.Add($"지역 간 재배치 {moves}명");
        return result;
    }

    // 지역마다 차례로 돌린다. 자동이면 지역마다 배치 방식 셋을 다 해 보고 가장 나은 쪽을 고른다
    // (실제로도 1지역은 나눠 세우고 2지역은 본진 앞에 모은다).
    private static NightRun RunGroups(BalanceBattleAssets assets, List<BalanceNightPlan> plans, List<List<int>> activeLanes,
        List<List<BalanceFieldHero>> groups, string seed, int day)
    {
        bool auto = assets.Placement == BalancePlacement.Auto;
        BalancePlacement[] modes = auto ? ManualPlacements : new[] { assets.Placement };
        var run = new NightRun { Total = new BalanceNightResult { Placement = assets.Placement } };
        // 마나는 지역 전체가 한 통이다. 앞 지역(1지역부터)이 쓴 스킬을 기록해 두고 다음 지역은 남은 마나로 쓴다.
        ManaLedger mana = NewLedger(assets);
        for (int index = 0; index < plans.Count; index++)
        {
            BalanceNightResult best = null;
            BalancePlacement bestPlacement = modes[0];
            ManaLedger bestMana = mana;
            int bestUnplaced = 0;
            foreach (BalancePlacement placement in modes)
            {
                List<BalanceFieldHero> placed = PlaceRegion(plans[index], activeLanes[index], groups[index], placement);
                var region = new BalanceNightResult();
                ManaLedger tried = mana?.Copy();
                RunRegion(plans[index], activeLanes[index], placed, seed, day, region, SkillsFor(assets, tried));
                if (best != null && !IsBetter(region, best)) continue;
                best = region;
                bestPlacement = placement;
                bestMana = tried;
                bestUnplaced = groups[index].Count - placed.Count;
            }
            mana = bestMana;
            run.Regions.Add(best);
            run.Unplaced += bestUnplaced;
            AddRegion(run.Total, best, auto ? bestPlacement : null);
        }
        return run;
    }

    // 지역 하나의 결과를 밤 전체 합에 더한다. 자동이면 요약 앞에 그 지역에서 고른 배치 방식을 붙인다.
    private static void AddRegion(BalanceNightResult night, BalanceNightResult region, BalancePlacement? placement)
    {
        night.Spawned += region.Spawned;
        night.Killed += region.Killed;
        night.Leaked += region.Leaked;
        night.BaseDamage += region.BaseDamage;
        night.BossLeaked |= region.BossLeaked;
        night.BossSpawned |= region.BossSpawned;
        night.BossKilled |= region.BossKilled;
        night.HeroDeaths += region.HeroDeaths;
        night.TimedOut |= region.TimedOut;
        night.BossNote ??= region.BossNote;
        string prefix = placement != null ? $"[{PlacementName(placement.Value)}] " : string.Empty;
        foreach (string note in region.RegionNotes) night.RegionNotes.Add(prefix + note);
    }

    private static readonly BalancePlacement[] ManualPlacements =
        { BalancePlacement.Entrance, BalancePlacement.Spread, BalancePlacement.Core };

    // 보스 누수 없음 -> 본진 피해 적음 -> 보스 처치 -> 영웅 사망 적음 -> 처치 많음 순으로 비교.
    private static bool IsBetter(BalanceNightResult night, BalanceNightResult best)
    {
        if (night.BossLeaked != best.BossLeaked) return !night.BossLeaked;
        if (night.BaseDamage != best.BaseDamage) return night.BaseDamage < best.BaseDamage;
        if (night.BossKilled != best.BossKilled) return night.BossKilled;
        if (night.HeroDeaths != best.HeroDeaths) return night.HeroDeaths < best.HeroDeaths;
        return night.Killed > best.Killed;
    }

    public static string PlacementName(BalancePlacement placement) => placement switch
    {
        BalancePlacement.Entrance => "입구 집중",
        BalancePlacement.Spread => "경로 분산",
        BalancePlacement.Core => "본진 쪽",
        _ => "자동",
    };

    // SpawnerManager.ShowPortal: 보스 밤 1번 지역은 가장 긴 레인 하나, 아니면 포탈 표 개수만큼 섞어서 고른다(시드 고정).
    private static List<int> PickActiveLanes(BalanceNightPlan plan, string seed, int day)
    {
        int laneCount = plan.Region.GroundLanes.Count;
        var lanes = new List<int>(laneCount);
        for (int index = 0; index < laneCount; index++) lanes.Add(index);
        if (laneCount == 0) return lanes;

        if (plan.CornerPortal)
        {
            int longest = 0;
            for (int index = 1; index < laneCount; index++)
            {
                if (plan.Region.GroundLanes[index].Count > plan.Region.GroundLanes[longest].Count) longest = index;
            }
            return new List<int> { longest };
        }

        var rng = new System.Random(GameSeeding.Derive($"{seed}{PortalSeedSuffix}:{plan.Region.Region}", day));
        int take = Mathf.Clamp(plan.PortalCount, 1, laneCount);
        for (int index = 0; index < take; index++)
        {
            int swap = rng.Next(index, laneCount);
            (lanes[index], lanes[swap]) = (lanes[swap], lanes[index]);
        }
        return lanes.GetRange(0, take);
    }

    // ── 배치 ──

    // 전투에 낼 영웅 전부. 근접(저지 자리)을 먼저 세워야 원거리가 그 칸을 사거리에 넣는 자리를 고를 수 있어 근접 먼저, 같은 종류 안에서는 강한 순.
    private static List<BalanceFieldHero> BuildRoster(BalanceHeroWorld heroes, BalanceBattleAssets assets)
    {
        var roster = new List<BalanceFieldHero>();
        foreach (BalanceHero hero in heroes.Roster)
        {
            BalanceFieldHero field = BuildFieldHero(heroes, hero, assets);
            if (field != null) roster.Add(field);
        }
        SortForPlacement(roster);
        return roster;
    }

    private static void SortForPlacement(List<BalanceFieldHero> heroes) =>
        heroes.Sort((left, right) => left.IsMelee != right.IsMelee ? right.IsMelee.CompareTo(left.IsMelee) : right.Dps.CompareTo(left.Dps));

    // 강한 영웅부터 위협(체력×마릿수) 대비 배치 전력이 가장 모자란 지역에 나눈다. 어느 지역에도 칸이 없으면 인벤토리에 남는다.
    private static List<List<BalanceFieldHero>> Assign(List<BalanceFieldHero> roster, List<BalanceNightPlan> plans, out int inventory)
    {
        var groups = new List<List<BalanceFieldHero>>();
        var threat = new List<float>();
        var assigned = new List<float>();
        foreach (BalanceNightPlan plan in plans)
        {
            groups.Add(new List<BalanceFieldHero>());
            float total = 0f;
            foreach (BalanceSpawnGroup group in plan.Groups) total += group.Hp * group.Count;
            threat.Add(total);
            assigned.Add(0f);
        }

        inventory = 0;
        foreach (BalanceFieldHero field in roster)
        {
            int region = PickRegion(plans, groups, threat, assigned, field);
            if (region < 0)
            {
                inventory++;
                continue;
            }
            groups[region].Add(field);
            assigned[region] += field.Dps;
        }
        return groups;
    }

    // 지역 하나에 나뉜 영웅을 칸에 세운다. 칸이 모자라 못 선 영웅은 돌려준 목록에서 빠진다.
    private static List<BalanceFieldHero> PlaceRegion(BalanceNightPlan plan, List<int> activeLanes, List<BalanceFieldHero> group,
        BalancePlacement placement)
    {
        var ordered = new List<BalanceFieldHero>(group);
        SortForPlacement(ordered);
        var used = new HashSet<Vector2Int>();
        var placed = new List<BalanceFieldHero>();
        foreach (BalanceFieldHero field in ordered)
        {
            if (!HasFreeTile(plan.Region, used, field.IsMelee)) continue;
            field.Tile = BestTile(plan, activeLanes, used, placed, field, placement);
            used.Add(field.Tile);
            placed.Add(field);
        }
        return placed;
    }

    // 위협은 그 영웅이 때릴 수 있는 적만 센다 - 공중만 나오는 지역에 근접을 보내면 막지도 때리지도 못한다.
    // 어느 지역에서도 때릴 적이 없으면 전체 위협 기준으로 놓는다(필드에 두는 편이 인벤토리보다 낫다).
    private static int PickRegion(List<BalanceNightPlan> plans, List<List<BalanceFieldHero>> groups, List<float> threat,
        List<float> assigned, BalanceFieldHero field)
    {
        int best = -1;
        float bestNeed = 0f;
        for (int index = 0; index < plans.Count; index++)
        {
            if (!HasRoom(plans[index].Region, groups[index], field.IsMelee)) continue;
            float need = UsefulThreat(plans[index], field) / (assigned[index] + 1f);
            if (need <= bestNeed) continue;
            bestNeed = need;
            best = index;
        }
        if (best >= 0) return best;

        float bestTotal = float.MinValue;
        for (int index = 0; index < plans.Count; index++)
        {
            if (!HasRoom(plans[index].Region, groups[index], field.IsMelee)) continue;
            float need = threat[index] / (assigned[index] + 1f);
            if (need <= bestTotal) continue;
            bestTotal = need;
            best = index;
        }
        return best;
    }

    // 그 종류가 설 수 있는 칸 수보다 적게 나뉘었는지.
    private static bool HasRoom(BalanceRegion region, List<BalanceFieldHero> group, bool melee)
    {
        int count = 0;
        foreach (BalanceFieldHero hero in group)
        {
            if (hero.IsMelee == melee) count++;
        }
        return count < (melee ? region.MeleeTiles.Count : region.RangedTiles.Count);
    }

    // 가장 많이 샌 지역에, 안 샌 지역에서 그 지역 적을 때릴 수 있는 영웅 한 명을 옮긴 배분들.
    // 약한 영웅부터 해 본다(주는 쪽 손실이 작다). 나아지면 다음 바퀴에 또 옮긴다.
    private static IEnumerable<List<List<BalanceFieldHero>>> MoveCandidates(NightRun run, List<List<BalanceFieldHero>> groups,
        List<BalanceNightPlan> plans)
    {
        int receiver = -1;
        for (int index = 0; index < run.Regions.Count; index++)
        {
            if (!Leaky(run.Regions[index])) continue;
            if (receiver < 0 || IsBetter(run.Regions[receiver], run.Regions[index])) receiver = index;
        }
        if (receiver < 0) yield break;

        var options = new List<(int Donor, BalanceFieldHero Hero)>();
        for (int donor = 0; donor < run.Regions.Count; donor++)
        {
            if (donor == receiver || Leaky(run.Regions[donor])) continue;
            foreach (BalanceFieldHero hero in groups[donor])
            {
                if (UsefulThreat(plans[receiver], hero) > 0f && HasRoom(plans[receiver].Region, groups[receiver], hero.IsMelee)) options.Add((donor, hero));
            }
        }
        options.Sort((left, right) => left.Hero.Dps.CompareTo(right.Hero.Dps));

        for (int trial = 0; trial < options.Count && trial < MaxMoveTrials; trial++)
        {
            var moved = groups.ConvertAll(group => new List<BalanceFieldHero>(group));
            moved[options[trial].Donor].Remove(options[trial].Hero);
            moved[receiver].Add(options[trial].Hero);
            yield return moved;
        }
    }

    private static bool Leaky(BalanceNightResult region) => region.BossLeaked || region.BaseDamage > 0;

    // CanTarget과 같은 기준(못 때리는 특성, 은신은 저지당하면 맞으니 제외 안 함).
    private static float UsefulThreat(BalanceNightPlan plan, BalanceFieldHero field)
    {
        float total = 0f;
        foreach (BalanceSpawnGroup group in plan.Groups)
        {
            if ((group.Attribute & field.Unattackable & ~EnemyAttribute.Cloaking) != 0) continue;
            total += group.Hp * group.Count;
        }
        return total;
    }

    private static bool HasFreeTile(BalanceRegion region, HashSet<Vector2Int> used, bool melee)
    {
        foreach (Vector2Int tile in melee ? region.MeleeTiles : region.RangedTiles)
        {
            if (!used.Contains(tile)) return true;
        }
        return false;
    }

    // 근접: 활성 레인이 많이 지나가고 정한 지점(입구 또는 본진 쪽 2/3)에 가까운 레인 칸(저지 자리). 레인 위 칸이 없으면 원거리처럼 고른다.
    //   경로 분산은 지점 대신 이미 선 근접과 경로상 멀리 떨어진 칸을 고른다.
    // 원거리: 사거리에 들어오는 활성 레인 칸 수 + 이미 놓인 저지 칸을 사거리에 넣은 만큼 가산.
    private static Vector2Int BestTile(BalanceNightPlan plan, List<int> activeLanes, HashSet<Vector2Int> used,
        List<BalanceFieldHero> placed, BalanceFieldHero field, BalancePlacement placement)
    {
        BalanceRegion region = plan.Region;
        float blockBonus = placement == BalancePlacement.Core ? CoreBlockCoverageBonus : EntranceBlockCoverageBonus;
        Vector2Int best = default;
        float bestScore = float.MinValue;
        foreach (Vector2Int tile in field.IsMelee ? region.MeleeTiles : region.RangedTiles)
        {
            if (used.Contains(tile)) continue;
            float score = !field.IsMelee ? 0f
                : placement == BalancePlacement.Spread ? SpreadScore(region, activeLanes, placed, tile)
                : BlockScore(region, activeLanes, tile, placement == BalancePlacement.Core ? CoreLanePreference : EntranceLanePreference);
            score += CoverageScore(region, activeLanes, placed, field, tile, blockBonus);
            if (score <= bestScore) continue;
            bestScore = score;
            best = tile;
        }
        return best;
    }

    private static float BlockScore(BalanceRegion region, List<int> activeLanes, Vector2Int tile, float lanePreference)
    {
        float score = 0f;
        foreach (int lane in activeLanes)
        {
            List<Vector2Int> path = region.GroundLanes[lane];
            int index = path.IndexOf(tile);
            if (index < 0) continue;
            float position = path.Count > 1 ? (float)index / (path.Count - 1) : 0f;
            score += LaneTileScore * (1f - Mathf.Abs(position - lanePreference));
        }
        return score;
    }

    // 경로 분산: 지나가는 활성 레인 수가 우선(갈라진 레인이 합쳐진 뒤에 서면 한 명이 모든 포탈을 막는다).
    // 그다음 이미 선 근접과 경로상 거리가 먼 칸 - 앞 근접을 지나친 적을 뒤 근접이 다시 막는다. 첫 근접은 입구 쪽.
    private static float SpreadScore(BalanceRegion region, List<int> activeLanes, List<BalanceFieldHero> placed, Vector2Int tile)
    {
        float score = 0f;
        foreach (int lane in activeLanes)
        {
            List<Vector2Int> path = region.GroundLanes[lane];
            int index = path.IndexOf(tile);
            if (index < 0) continue;

            int gap = path.Count;
            foreach (BalanceFieldHero other in placed)
            {
                if (!other.IsMelee) continue;
                int otherIndex = path.IndexOf(other.Tile);
                if (otherIndex >= 0) gap = Mathf.Min(gap, Mathf.Abs(index - otherIndex));
            }
            float position = path.Count > 1 ? (float)index / (path.Count - 1) : 0f;
            score += LaneTileScore + SpreadGapScore * gap / path.Count + SpreadEntranceScore * (1f - position);
        }
        return score;
    }

    private static float CoverageScore(BalanceRegion region, List<int> activeLanes, List<BalanceFieldHero> placed,
        BalanceFieldHero field, Vector2Int tile, float blockBonus)
    {
        float score = 0f;
        foreach (int lane in activeLanes)
        {
            foreach (Vector2Int laneTile in region.GroundLanes[lane])
            {
                if (BalanceRange.Contains(tile, laneTile, field.Range, field.Shape)) score += 1f;
            }
        }
        foreach (BalanceFieldHero other in placed)
        {
            if (other.IsMelee && BalanceRange.Contains(tile, other.Tile, field.Range, field.Shape)) score += blockBonus;
        }
        return score;
    }

    // 영웅 하나의 전투용 값. 공격 모양(사거리·범위·타겟 수·못 때리는 특성)은 평타 패턴의 첫 공격 기준.
    private static BalanceFieldHero BuildFieldHero(BalanceHeroWorld heroes, BalanceHero hero, BalanceBattleAssets assets)
    {
        HeroSimEntry entry = heroes.EntryOf(hero);
        HeroSimResult stats = heroes.StatsOf(hero);
        if (entry == null || stats == null || entry.BasePattern.Count == 0 || stats.AttackSpeed <= 0f) return null;

        AttackDataSO primary = entry.BasePattern[0];
        return new BalanceFieldHero
        {
            Hero = hero,
            Entry = entry,
            Heavies = HeroSimCalc.ResolveHeavyRatios(entry),
            IsMelee = hero.Kind == OccupantKind.MeleeHero,
            MaxHp = stats.MaxHp * assets.HeroHpPercent / PercentScale,
            Hp = stats.MaxHp * assets.HeroHpPercent / PercentScale,
            DamageScale = (float)assets.HeroDamagePercent / PercentScale,
            Attack = stats.AttackPower,
            Defense = stats.Defence,
            AttackInterval = 1f / stats.AttackSpeed,
            BlockCount = Mathf.RoundToInt(stats.BlockCount),
            Range = primary.range,
            Shape = primary.rangeShape,
            AttackType = primary.attackType,
            TargetCount = Mathf.Max(1, primary.targetCount),
            AreaRange = primary.areaRange,
            HitsPerAttack = Mathf.Max(1, primary.attackCount),
            Unattackable = primary.unattackableTarget,
            AreaUnattackable = primary.AreaUnattackableTarget,
            Dps = stats.DamagePerSecond,
        };
    }

    // ── 지역 하나의 밤 ──

    private class BattleEnemy
    {
        public BalanceSpawnGroup Group;
        public List<Vector2Int> Path;
        public float SpawnTime;
        public float Progress;      // 경로 위 위치(칸 단위, 0 = 스폰 칸)
        public float Hp;
        public float Cooldown;
        public bool Spawned;
        public bool Done;           // 죽었거나 새어 나갔다
        public BalanceFieldHero Blocker;
        public float BlockedSeconds;  // 저지당해 멈춰 있던 시간(보스 상세용)
        public int MaxHeroesInRange;  // 한 번에 이 적을 사거리에 넣은 영웅 수의 최댓값(보스 상세용)
        public float SlowedUntil;     // 플레이어 둔화 장판이 마지막으로 건 둔화가 풀리는 시각
        public float SpeedScale = 1f; // 이번 칸의 이속 배율(둔화 중이면 장판 값)

        public Vector2Int Tile => Path[Mathf.Min(Mathf.FloorToInt(Progress), Path.Count - 1)];
        public float Remaining => Path.Count - 1 - Progress;
        public bool Has(EnemyAttribute attribute) => (Group.Attribute & attribute) != 0;
        public bool Active => Spawned && !Done;
    }

    private static void RunRegion(BalanceNightPlan plan, List<int> activeLanes, List<BalanceFieldHero> heroes,
        string seed, int day, BalanceNightResult result, SkillCaster skills)
    {
        if (activeLanes.Count == 0)
        {
            result.RegionNotes.Add($"지역{plan.Region.Region} 레인 없음 - 적 무시됨");
            return;
        }
        List<BattleEnemy> enemies = SpawnSchedule(plan, activeLanes, seed, day);
        var meleeByTile = new Dictionary<Vector2Int, BalanceFieldHero>();
        foreach (BalanceFieldHero hero in heroes)
        {
            hero.Hp = hero.MaxHp;
            hero.Cooldown = 0f;
            hero.Blocking = 0;
            hero.Target = null;
            hero.SkillAttackScale = 1f;
            hero.SkillAttackSpeedAdd = 0f;
            if (hero.IsMelee) meleeByTile[hero.Tile] = hero;
        }

        int leakedBefore = result.Leaked;
        int deathsBefore = result.HeroDeaths;
        int remaining = enemies.Count;
        result.Spawned += enemies.Count;
        for (float time = 0f; remaining > 0 && time < MaxNightSeconds; time += StepSeconds)
        {
            foreach (BattleEnemy enemy in enemies)
            {
                if (!enemy.Spawned && enemy.SpawnTime <= time) enemy.Spawned = true;
            }
            skills?.Step(time, plan.Region, activeLanes, heroes, enemies, ref remaining, result);
            foreach (BalanceFieldHero hero in heroes)
            {
                if (hero.Alive) HeroAct(hero, enemies, ref remaining, result);
            }
            foreach (BattleEnemy enemy in enemies)
            {
                if (enemy.Active) EnemyAct(enemy, heroes, meleeByTile, ref remaining, result);
            }
        }

        // 제한 시간이 지나도 남은 적은 결국 본진에 닿는다고 본다.
        foreach (BattleEnemy enemy in enemies)
        {
            if (enemy.Done) continue;
            result.TimedOut = true;
            Leak(enemy, result);
        }
        result.RegionNotes.Add($"지역{plan.Region.Region} 누수 {result.Leaked - leakedBefore}/{enemies.Count} " +
            $"(근접 {meleeByTile.Count}·저지 칸 {BlockingTiles(meleeByTile, plan.Region, activeLanes)}·원거리 {heroes.Count - meleeByTile.Count}·영웅 사망 {result.HeroDeaths - deathsBefore}" +
            $"{skills?.Summary()})");
    }

    // 근접 영웅 중 오늘 활성 레인 위에 선 수(실제로 저지할 수 있는 영웅).
    private static int BlockingTiles(Dictionary<Vector2Int, BalanceFieldHero> meleeByTile, BalanceRegion region, List<int> activeLanes)
    {
        int count = 0;
        foreach (Vector2Int tile in meleeByTile.Keys)
        {
            foreach (int lane in activeLanes)
            {
                if (!region.GroundLanes[lane].Contains(tile)) continue;
                count++;
                break;
            }
        }
        return count;
    }

    // WaveSpawner.SpawnWaveRout: 줄마다 StartTime부터 Delay 간격으로 한 마리씩, 마리마다 활성 포탈 중 하나를 시드로 고른다.
    // 공중은 그 포탈에 저작 공중 경로가 있으면 그걸, 헤엄은 헤엄 경로, 나머지는 지상 레인을 따른다.
    private static List<BattleEnemy> SpawnSchedule(BalanceNightPlan plan, List<int> activeLanes, string seed, int day)
    {
        var enemies = new List<BattleEnemy>();
        var rng = new System.Random(GameSeeding.Derive($"{seed}{PathSeedSuffix}:{plan.Region.Region}", day));
        foreach (BalanceSpawnGroup group in plan.Groups)
        {
            for (int index = 0; index < group.Count; index++)
            {
                int lane = activeLanes[rng.Next(activeLanes.Count)];
                enemies.Add(new BattleEnemy
                {
                    Group = group,
                    Path = PathFor(plan.Region, lane, group, rng),
                    SpawnTime = group.StartTime + index * group.Delay,
                    Hp = group.Hp,
                });
            }
        }
        return enemies;
    }

    private static List<Vector2Int> PathFor(BalanceRegion region, int lane, BalanceSpawnGroup group, System.Random rng)
    {
        List<Vector2Int> ground = region.GroundLanes[lane];
        if ((group.Attribute & EnemyAttribute.Fly) != 0 && region.AirRoutes.TryGetValue(ground[0], out List<List<Vector2Int>> air) && air.Count > 0)
            return air[rng.Next(air.Count)];
        if ((group.Attribute & EnemyAttribute.Swim) != 0 && region.SwimLanes[lane].Count > 0)
            return region.SwimLanes[lane];
        return ground;
    }

    // 쿨다운이 돌았으면 사거리 안에서 본진에 가장 가까운 적을 친다. 범위 공격은 그 적 주변 areaRange 칸의 적도 같이 맞는다.
    private static void HeroAct(BalanceFieldHero hero, List<BattleEnemy> enemies, ref int remaining, BalanceNightResult result)
    {
        hero.Cooldown -= StepSeconds;
        if (hero.Cooldown > 0f) return;

        var targets = new List<BattleEnemy>();
        foreach (BattleEnemy enemy in enemies)
        {
            if (CanTarget(hero, enemy)) targets.Add(enemy);
        }
        if (targets.Count == 0)
        {
            hero.Cooldown = 0f;
            return;
        }

        // Hero.Update: 잡은 대상이 사거리에 있으면 계속 치고, 없으면 영웅에게 가장 가까운 적을 새로 잡는다(AcquireTargetFromTiles).
        // 그래서 근접은 자기가 막은 적(같은 칸)을 먼저 잡는다 - 본진에 가까운 순으로 치면 지나가는 적만 쫓다 둘 다 놓친다.
        targets.Sort((left, right) => DistanceSquared(hero.Tile, left.Tile).CompareTo(DistanceSquared(hero.Tile, right.Tile)));
        if (hero.Target is BattleEnemy current && targets.Remove(current)) targets.Insert(0, current);
        hero.Target = targets[0];
        int centers = hero.AttackType == AttackType.Area ? 1 : Mathf.Min(hero.TargetCount, targets.Count);
        for (int index = 0; index < centers; index++)
        {
            if (hero.AttackType == AttackType.Area) HitArea(hero, targets[index], enemies, ref remaining, result);
            else Hit(hero, targets[index], ref remaining, result);
        }
        // 공격 버프의 AS Flat 가산: 간격 = 1 / (공속 + 가산).
        hero.Cooldown += hero.SkillAttackSpeedAdd > 0f ? 1f / (1f / hero.AttackInterval + hero.SkillAttackSpeedAdd) : hero.AttackInterval;
    }

    private static int DistanceSquared(Vector2Int a, Vector2Int b) => (a - b).sqrMagnitude;

    // 못 때리는 특성(기본: 공중·은신)이면 제외. 은신·잠행은 저지당했을 때만 맞는다(EnemyAttribute 주석).
    private static bool CanTarget(BalanceFieldHero hero, BattleEnemy enemy)
    {
        if (!enemy.Active) return false;
        if ((enemy.Group.Attribute & hero.Unattackable & ~EnemyAttribute.Cloaking) != 0) return false;
        bool hidden = enemy.Has(EnemyAttribute.Cloaking) || enemy.Has(EnemyAttribute.Burrow);
        if (hidden && enemy.Blocker == null) return false;
        return BalanceRange.Contains(hero.Tile, enemy.Tile, hero.Range, hero.Shape);
    }

    private static void HitArea(BalanceFieldHero hero, BattleEnemy center, List<BattleEnemy> enemies, ref int remaining, BalanceNightResult result)
    {
        Vector2Int origin = center.Tile;
        foreach (BattleEnemy enemy in enemies)
        {
            if (!enemy.Active || (enemy.Group.Attribute & hero.AreaUnattackable) != 0) continue;
            if (BalanceRange.Manhattan(origin, enemy.Tile) <= hero.AreaRange) Hit(hero, enemy, ref remaining, result);
        }
    }

    // 1타 평균 피해는 영웅 시뮬레이터와 같은 HeroSimCalc(방어력 반영). 타수 보호막은 타당 1만 깎인다(EnemyBase.TakeDamage).
    private static void Hit(BalanceFieldHero hero, BattleEnemy enemy, ref int remaining, BalanceNightResult result)
    {
        int defense = Mathf.RoundToInt(enemy.Group.Defense);
        var key = (defense, hero.SkillAttackScale);
        if (!hero.HitByDefense.TryGetValue(key, out float hit))
        {
            hit = HeroSimCalc.CalculateAverageHit(hero.Entry.BasePattern, hero.Heavies, hero.Attack * hero.SkillAttackScale, defense) * hero.DamageScale;
            hero.HitByDefense[key] = hit;
        }
        enemy.Hp -= enemy.Has(EnemyAttribute.HitsShield) ? hero.HitsPerAttack : hit;
        if (enemy.Hp <= 0f) KillEnemy(enemy, ref remaining, result);
    }

    private static void KillEnemy(BattleEnemy enemy, ref int remaining, BalanceNightResult result)
    {
        enemy.Done = true;
        remaining--;
        result.Killed++;
        if (enemy.Group.Class == EnemyClass.Boss)
        {
            result.BossKilled = true;
            result.BossNote = BossDetail(enemy, "처치");
        }
        ReleaseBlock(enemy);
    }

    // 저지당했으면 막은 영웅을, 원거리면 사거리 안 가장 가까운 영웅을 친다(근접은 저지당했을 때만 공격 - EnemyBase.Attack).
    private static void EnemyAct(BattleEnemy enemy, List<BalanceFieldHero> heroes, Dictionary<Vector2Int, BalanceFieldHero> meleeByTile,
        ref int remaining, BalanceNightResult result)
    {
        if (enemy.Group.Class == EnemyClass.Boss) result.BossSpawned = true;
        if (enemy.Blocker != null && !enemy.Blocker.Alive) enemy.Blocker = null;
        if (enemy.Blocker != null) enemy.BlockedSeconds += StepSeconds;
        if (enemy.Group.Class == EnemyClass.Boss) enemy.MaxHeroesInRange = Mathf.Max(enemy.MaxHeroesInRange, HeroesInRange(enemy, heroes));

        BalanceFieldHero target = enemy.Blocker ?? (enemy.Group.Type == EnemyType.Ranged ? NearestHero(enemy, heroes) : null);
        enemy.Cooldown -= StepSeconds;
        if (target != null && enemy.Cooldown <= 0f)
        {
            target.Hp -= Mathf.Max(1f, Mathf.Floor(enemy.Group.Attack) - target.Defense); // Hero.TakeDamage
            enemy.Cooldown += enemy.Group.AttackCycle; // EnemyBase.RunAttackLoop: 모션 중엔 타이머가 멈춘다
            if (!target.Alive) HeroDied(target, result);
        }
        if (target == null) enemy.Cooldown = Mathf.Max(enemy.Cooldown, 0f);
        if (enemy.Blocker != null) return;

        float speed = enemy.Group.TilesPerSecond * enemy.SpeedScale * (target != null ? enemy.Group.MovingShare : 1f);
        Move(enemy, speed * StepSeconds, meleeByTile, ref remaining, result);
    }

    // 지금 이 적을 때릴 수 있는 영웅 수(사거리·못 때리는 특성 반영).
    private static int HeroesInRange(BattleEnemy enemy, List<BalanceFieldHero> heroes)
    {
        int count = 0;
        foreach (BalanceFieldHero hero in heroes)
        {
            if (hero.Alive && CanTarget(hero, enemy)) count++;
        }
        return count;
    }

    private static string BossDetail(BattleEnemy enemy, string outcome) =>
        $"보스 {outcome}: 남은 체력 {Mathf.Max(0f, enemy.Hp):N0}/{enemy.Group.Hp:N0} · 막힌 시간 {enemy.BlockedSeconds:N0}초 · " +
        $"동시에 때린 영웅 최대 {enemy.MaxHeroesInRange}명 · 경로 {enemy.Path.Count}칸";

    private static BalanceFieldHero NearestHero(BattleEnemy enemy, List<BalanceFieldHero> heroes)
    {
        BalanceFieldHero nearest = null;
        int bestDistance = int.MaxValue;
        foreach (BalanceFieldHero hero in heroes)
        {
            if (!hero.Alive) continue;
            int distance = BalanceRange.Manhattan(enemy.Tile, hero.Tile);
            if (distance > enemy.Group.Enemy.Range || distance >= bestDistance) continue;
            bestDistance = distance;
            nearest = hero;
        }
        return nearest;
    }

    // 지금 칸 또는 새로 들어서는 칸의 근접 영웅이 아직 막을 자리가 있으면 멈춘다(BlockCalc: 그 칸에 있는 적 중 순서 < BLK).
    // 지금 칸도 보는 이유: 게임은 매 프레임 다시 판정해서, 막혀 있던 적이 죽으면 그 칸을 지나던 적이 바로 저지된다.
    // 공중·저지 불가 적은 막히지 않는다. 경로 끝에 닿으면 본진 피해.
    private static void Move(BattleEnemy enemy, float distance, Dictionary<Vector2Int, BalanceFieldHero> meleeByTile,
        ref int remaining, BalanceNightResult result)
    {
        bool blockable = !enemy.Has(EnemyAttribute.Fly) && !enemy.Has(EnemyAttribute.UnJudged);
        int from = Mathf.FloorToInt(enemy.Progress);
        enemy.Progress += distance;
        int to = Mathf.Min(Mathf.FloorToInt(enemy.Progress), enemy.Path.Count - 1);

        for (int index = from; blockable && index <= to; index++)
        {
            if (!meleeByTile.TryGetValue(enemy.Path[index], out BalanceFieldHero blocker)) continue;
            if (!blocker.Alive || blocker.Blocking >= blocker.BlockCount) continue;
            enemy.Progress = index;
            enemy.Blocker = blocker;
            blocker.Blocking++;
            return;
        }

        if (enemy.Remaining > 0f) return;
        remaining--;
        Leak(enemy, result);
    }

    private static void Leak(BattleEnemy enemy, BalanceNightResult result)
    {
        enemy.Done = true;
        result.Leaked++;
        if (enemy.Group.Class == EnemyClass.Boss) result.BossNote = BossDetail(enemy, "누수");
        switch (enemy.Group.Class)
        {
            case EnemyClass.Boss: result.BossLeaked = true; break;
            case EnemyClass.Elite: result.BaseDamage += EliteLeakDamage; break;
            default: result.BaseDamage += NormalLeakDamage; break;
        }
    }

    private static void ReleaseBlock(BattleEnemy enemy)
    {
        if (enemy.Blocker == null) return;
        // 막던 영웅이 이미 쓰러졌으면 HeroDied가 카운트를 0으로 비웠다 - 다시 빼면 음수가 된다.
        if (enemy.Blocker.Alive) enemy.Blocker.Blocking--;
        enemy.Blocker = null;
    }

    // 영웅이 쓰러지면 그 밤 동안은 빠지고(다음 날 아침 Hero.Resurrection), 막고 있던 적은 다시 걷는다.
    private static void HeroDied(BalanceFieldHero hero, BalanceNightResult result)
    {
        result.HeroDeaths++;
        hero.Blocking = 0;
    }
}
