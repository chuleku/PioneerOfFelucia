using System.Collections.Generic;
using UnityEngine;

// 플레이어 스킬(밤에 마나로 까는 장판, PlayerGroundZoneEffect)을 전투에 넣는다.
// 언제 어디에 쓸지는 실제 플레이 기록(1~30일차)에 맞춘다 - 공격 버프 위주, 위험해 보이는 지역에 마나가 되는 대로.
// 마나는 막기 힘든 지역이 먼저 쓴다(RunGroups가 그 순서로 돌린다). 우선순위 공격 버프 > 힐 > 둔화:
//   버프 - 보스가 사거리에 들거나, 사거리 안 적이 3마리 이상이거나, 막히지 않은 적이 경로 절반을 넘으면
//          화력(DPS) 합이 가장 큰 영웅 칸에(이미 버프 장판이 덮은 칸은 빼고)
//   힐   - 영웅 체력이 40% 아래로 떨어지면, 다친 양이 가장 많이 들어가는 영웅 칸에(버프를 안 쓸 때만)
//   둔화 - 보스가 막히지 않고 지나갈 때만, 영웅 사거리가 닿는 그 칸에
public static partial class BalanceBattle
{
    private const float HealHpRatio = 0.4f;
    private const int BuffMinEnemies = 3;
    private const float LeakRiskProgress = 0.5f;
    private const float ManaEpsilon = 0.001f;
    private const float MinTickSeconds = 0.05f;    // tickInterval 0(매 프레임) 장판이 무한 반복되지 않게
    private const float DecisionSeconds = 0.5f;    // 스킬을 쓸지 이 간격으로만 따진다(사람 반응 속도 + 계산량)

    private static ManaLedger NewLedger(BalanceBattleAssets assets) =>
        assets.UsePlayerSkills && assets.PlayerSkills.Problem == null
            ? new ManaLedger(assets.PlayerSkills.MaxMana, assets.PlayerSkills.ManaRegen)
            : null;

    private static SkillCaster SkillsFor(BalanceBattleAssets assets, ManaLedger mana) =>
        mana == null ? null : new SkillCaster(assets.PlayerSkills.Skills, mana);

    // 밤 하나의 마나 사용 기록. 마나는 지역 전체가 한 통인데 지역은 차례로 돌리므로, 시각별 사용을 모아 두고
    // 새 사용을 끼워 넣어도 어느 시점에 마나가 음수가 되지 않는지 처음부터 다시 따진다
    // (PlayerManaManager: 밤 시작 때 가득, 밤에만 초당 회복, 최대치에서 멈춤). 먼저 돌린 지역(막기 힘든 지역)이 먼저 쓴다.
    private class ManaLedger
    {
        private readonly float max;
        private readonly float regen;
        private readonly List<(float Time, float Cost)> spends = new();

        public ManaLedger(float max, float regen)
        {
            this.max = max;
            this.regen = regen;
        }

        public ManaLedger Copy()
        {
            var copy = new ManaLedger(max, regen);
            copy.spends.AddRange(spends);
            return copy;
        }

        public bool CanSpend(float time, float cost)
        {
            if (!TrySpend(time, cost)) return false;
            spends.RemoveAt(spends.FindLastIndex(spend => spend.Time == time && spend.Cost == cost));
            return true;
        }

        public bool TrySpend(float time, float cost)
        {
            int at = spends.FindIndex(spend => spend.Time > time);
            if (at < 0) at = spends.Count;
            spends.Insert(at, (time, cost));
            if (Valid()) return true;
            spends.RemoveAt(at);
            return false;
        }

        private bool Valid()
        {
            float mana = max;
            float last = 0f;
            foreach (var (time, cost) in spends)
            {
                mana = Mathf.Min(max, mana + regen * (time - last)) - cost;
                last = time;
                if (mana < -ManaEpsilon) return false;
            }
            return true;
        }
    }

    private class ActiveZone
    {
        public BalancePlayerSkill Skill;
        public Vector2Int Center;
        public float EndTime;
        public float NextTick;

        public bool Covers(Vector2Int tile) => BalanceRange.Contains(Center, tile, Skill.Radius, Skill.Shape);
    }

    // 지역 하나의 밤 동안 깔린 장판과 쓴 횟수.
    private class SkillCaster
    {
        private readonly List<BalancePlayerSkill> skills;
        private readonly ManaLedger mana;
        private readonly List<ActiveZone> zones = new();
        private readonly Dictionary<string, int> casts = new();
        private float nextDecision;

        public SkillCaster(List<BalancePlayerSkill> skills, ManaLedger mana)
        {
            this.skills = skills;
            this.mana = mana;
        }

        public void Step(float time, BalanceRegion region, List<int> activeLanes, List<BalanceFieldHero> heroes,
            List<BattleEnemy> enemies, ref int remaining, BalanceNightResult result)
        {
            zones.RemoveAll(zone => zone.EndTime <= time);
            ApplyZones(time, heroes, enemies, ref remaining, result);
            if (time < nextDecision) return;
            nextDecision = time + DecisionSeconds;
            if (TryBuff(time, heroes, enemies)) return;
            if (TryHeal(time, heroes)) return;
            TrySlow(time, heroes, enemies);
        }

        // " · 스킬 힐 1·공격 버프 2" (안 썼으면 " · 스킬 0").
        public string Summary()
        {
            if (casts.Count == 0) return "·스킬 0";
            var parts = new List<string>();
            foreach (var (label, count) in casts) parts.Add($"{label} {count}");
            return "·스킬 " + string.Join("·", parts);
        }

        // ── 효과 ──

        private void ApplyZones(float time, List<BalanceFieldHero> heroes, List<BattleEnemy> enemies, ref int remaining,
            BalanceNightResult result)
        {
            foreach (BalanceFieldHero hero in heroes)
            {
                hero.SkillAttackScale = 1f;
                hero.SkillAttackSpeedAdd = 0f;
            }
            foreach (BattleEnemy enemy in enemies)
            {
                if (time >= enemy.SlowedUntil) enemy.SpeedScale = 1f;
            }

            foreach (ActiveZone zone in zones)
            {
                BalancePlayerSkill skill = zone.Skill;
                switch (skill.Mode)
                {
                    // 범위 안에 있는 동안 유지(장판 인스턴스가 출처라 장판이 둘이면 Buff 층 Additive가 더해진다).
                    case GroundZoneMode.Buff:
                        foreach (BalanceFieldHero hero in heroes)
                        {
                            if (!hero.Alive || !zone.Covers(hero.Tile)) continue;
                            hero.SkillAttackScale += skill.AttackScale - 1f;
                            hero.SkillAttackSpeedAdd += skill.AttackSpeedAdd;
                        }
                        break;
                    case GroundZoneMode.Heal:
                        while (time >= zone.NextTick)
                        {
                            zone.NextTick += Mathf.Max(skill.TickInterval, MinTickSeconds);
                            foreach (BalanceFieldHero hero in heroes)
                            {
                                if (!hero.Alive || !zone.Covers(hero.Tile)) continue;
                                hero.Hp = Mathf.Min(hero.MaxHp, hero.Hp + skill.HealAmount + hero.MaxHp * skill.HpHealPer);
                            }
                        }
                        break;
                    default:
                        // 지상 장판은 공중 적을 못 건드린다(PlayerGroundZoneEffect.QueryEnemies(Fly)). 둔화는 틱마다 다시 걸려
                        // 범위 안에 있는 동안 유지되고, 나가면 SlowLinger 뒤에 풀린다.
                        bool damageTick = skill.DamageAmount > 0f && time >= zone.NextTick;
                        if (damageTick) zone.NextTick += Mathf.Max(skill.TickInterval, MinTickSeconds);
                        foreach (BattleEnemy enemy in enemies)
                        {
                            if (!enemy.Active || enemy.Has(EnemyAttribute.Fly) || !zone.Covers(enemy.Tile)) continue;
                            if (skill.Slows && !Immune(enemy, skill))
                            {
                                enemy.SlowedUntil = Mathf.Max(enemy.SlowedUntil, time + skill.SlowLinger);
                                enemy.SpeedScale = Mathf.Min(enemy.SpeedScale, skill.MoveScale);
                            }
                            if (!damageTick) continue;
                            enemy.Hp -= skill.DamageAmount;
                            if (enemy.Hp <= 0f) KillEnemy(enemy, ref remaining, result);
                        }
                        break;
                }
            }
        }

        private static bool Immune(BattleEnemy enemy, BalancePlayerSkill skill) => (enemy.Group.ImmuneDebuffs & skill.DebuffType) != 0;

        // ── 언제 어디에 쓸지 ──

        private bool TryHeal(float time, List<BalanceFieldHero> heroes)
        {
            BalancePlayerSkill skill = skills.Find(candidate => candidate.Mode == GroundZoneMode.Heal);
            if (skill == null || !mana.CanSpend(time, skill.ManaCost)) return false;

            Vector2Int? best = null;
            float bestMissing = 0f;
            foreach (BalanceFieldHero hurt in heroes)
            {
                if (!hurt.Alive || hurt.Hp >= hurt.MaxHp * HealHpRatio || Covered(GroundZoneMode.Heal, hurt.Tile)) continue;
                float missing = 0f;
                foreach (BalanceFieldHero hero in heroes)
                {
                    if (hero.Alive && BalanceRange.Contains(hurt.Tile, hero.Tile, skill.Radius, skill.Shape)) missing += hero.MaxHp - hero.Hp;
                }
                if (missing <= bestMissing) continue;
                bestMissing = missing;
                best = hurt.Tile;
            }
            return best != null && Cast(skill, best.Value, time);
        }

        // 위험해 보이는 지역: 보스가 사거리에 있거나, 사거리 안 적이 몰려 있거나, 막히지 않은 적이 경로 절반을 넘었다(곧 샌다).
        private static bool InDanger(List<BalanceFieldHero> heroes, List<BattleEnemy> enemies)
        {
            int inRange = 0;
            foreach (BattleEnemy enemy in enemies)
            {
                if (!enemy.Active) continue;
                if (enemy.Blocker == null && enemy.Progress >= (enemy.Path.Count - 1) * LeakRiskProgress) return true;
                if (!heroes.Exists(hero => hero.Alive && CanTarget(hero, enemy))) continue;
                if (enemy.Group.Class == EnemyClass.Boss) return true;
                inRange++;
            }
            return inRange >= BuffMinEnemies;
        }

        private bool TryBuff(float time, List<BalanceFieldHero> heroes, List<BattleEnemy> enemies)
        {
            BalancePlayerSkill skill = skills.Find(candidate => candidate.Mode == GroundZoneMode.Buff);
            if (skill == null || !mana.CanSpend(time, skill.ManaCost)) return false;
            if (!InDanger(heroes, enemies)) return false;

            Vector2Int? best = null;
            float bestDps = 0f;
            foreach (BalanceFieldHero center in heroes)
            {
                if (!center.Alive || Covered(GroundZoneMode.Buff, center.Tile)) continue;
                float dps = 0f;
                foreach (BalanceFieldHero hero in heroes)
                {
                    if (hero.Alive && BalanceRange.Contains(center.Tile, hero.Tile, skill.Radius, skill.Shape)) dps += hero.Dps;
                }
                if (dps <= bestDps) continue;
                bestDps = dps;
                best = center.Tile;
            }
            return best != null && Cast(skill, best.Value, time);
        }

        private bool TrySlow(float time, List<BalanceFieldHero> heroes, List<BattleEnemy> enemies)
        {
            BalancePlayerSkill skill = skills.Find(candidate => candidate.Mode == GroundZoneMode.Damage && (candidate.Slows || candidate.DamageAmount > 0f));
            if (skill == null || !mana.CanSpend(time, skill.ManaCost)) return false;

            Vector2Int? best = null;
            int bestCount = 0;
            foreach (BattleEnemy center in enemies)
            {
                if (!Slowable(center, skill) || Covered(GroundZoneMode.Damage, center.Tile)) continue;
                if (!heroes.Exists(hero => hero.Alive && BalanceRange.Contains(hero.Tile, center.Tile, hero.Range, hero.Shape))) continue;

                int count = 0;
                bool boss = false;
                foreach (BattleEnemy enemy in enemies)
                {
                    if (!Slowable(enemy, skill) || !BalanceRange.Contains(center.Tile, enemy.Tile, skill.Radius, skill.Shape)) continue;
                    count++;
                    if (enemy.Group.Class == EnemyClass.Boss) boss = true;
                }
                if (!boss || count <= bestCount) continue;
                bestCount = count;
                best = center.Tile;
            }
            return best != null && Cast(skill, best.Value, time);
        }

        // 막히지 않고 걷는 지상 적(공중은 지상 장판에 안 걸린다).
        private static bool Slowable(BattleEnemy enemy, BalancePlayerSkill skill) =>
            enemy.Active && enemy.Blocker == null && !enemy.Has(EnemyAttribute.Fly) && !(skill.Slows && Immune(enemy, skill));

        private bool Covered(GroundZoneMode mode, Vector2Int tile) => zones.Exists(zone => zone.Skill.Mode == mode && zone.Covers(tile));

        private bool Cast(BalancePlayerSkill skill, Vector2Int center, float time)
        {
            if (!mana.TrySpend(time, skill.ManaCost)) return false;
            zones.Add(new ActiveZone
            {
                Skill = skill,
                Center = center,
                EndTime = time + skill.Duration,
                NextTick = time + skill.TickInterval,
            });
            casts.TryGetValue(skill.Label, out int count);
            casts[skill.Label] = count + 1;
            return true;
        }
    }
}
