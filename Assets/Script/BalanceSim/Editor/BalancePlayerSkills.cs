using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 플레이어 스킬 하나 = PlayerSkillPanel 버튼 하나 = 장판 프리팹(PlayerGroundZoneEffect) 하나.
// 밤에만 쓰고, 고른 칸을 중심으로 duration 동안 범위 안 영웅/적에게 효과를 준다.
public class BalancePlayerSkill
{
    public string Label;
    public float ManaCost;
    public GroundZoneMode Mode;
    public RangeShape Shape;
    public int Radius;
    public float Duration;
    public float TickInterval;
    public float DamageAmount;        // Damage: 틱당 고정 피해(지상 적만)
    public float HealAmount;          // Heal: 틱당 고정 회복
    public float HpHealPer;           // Heal: 틱당 최대 체력 비례 회복
    public float AttackScale = 1f;    // Buff: ATK 배율(Buff 층 Additive는 장비·직업 강화(Equip 층)와 따로 곱해진다)
    public float AttackSpeedAdd;      // Buff: AS Flat 가산
    public float MoveScale = 1f;      // 디버프: 이속 배율(StatDebuffSO의 SPD 효과)
    public float SlowLinger;          // 디버프: 장판 틱마다 다시 거는 지속시간(durationOverride) - 장판을 벗어나도 이만큼 남는다
    public DebuffType DebuffType;     // 면역 판정용

    public bool Slows => MoveScale < 1f;
}

// 플레이어 마나와 스킬 목록. 마나는 밤 하나 동안 모든 지역이 같이 쓰는 한 통이다(PlayerManaManager).
public class BalancePlayerSkillSet
{
    public float MaxMana;
    public float ManaRegen;   // 초당, 밤에만
    public readonly List<BalancePlayerSkill> Skills = new();

    public string Problem =>
        MaxMana <= 0f ? "플레이어 마나를 못 읽음(GameLifeTimeScope의 playerManaManagerPrefab)" :
        Skills.Count == 0 ? "플레이어 스킬을 못 읽음(MainScene의 PlayerSkillPanel)" :
        null;
}

// MainScene에서 플레이어 스킬 값을 읽는다. 숫자는 프리팹·에셋에서 그대로 가져온다.
public static class BalancePlayerSkillSource
{
    private const string ManaPrefabField = "playerManaManagerPrefab";
    private const string MaxManaField = "maxMana";
    private const string ManaRegenField = "manaRegenPerSecond";
    private const string EntriesField = "entries";
    private const string SkillField = "skill";
    private const string ManaCostField = "manaCost";
    private const string ZonePrefabField = "zonePrefab";
    private const string BuffLabel = "공격 버프";
    private const string HealLabel = "힐";
    private const string SlowLabel = "둔화";
    private const string DamageLabel = "피해 장판";

    public static BalancePlayerSkillSet Read(GameLifeTimeScope scope, PlayerSkillPanel panel)
    {
        var set = new BalancePlayerSkillSet();
        if (scope != null && new SerializedObject(scope).FindProperty(ManaPrefabField)?.objectReferenceValue is PlayerManaManager mana)
        {
            var serialized = new SerializedObject(mana);
            set.MaxMana = serialized.FindProperty(MaxManaField).floatValue;
            set.ManaRegen = serialized.FindProperty(ManaRegenField).floatValue;
        }
        if (panel == null) return set;

        SerializedProperty entries = new SerializedObject(panel).FindProperty(EntriesField);
        for (int index = 0; index < entries.arraySize; index++)
        {
            SerializedProperty slot = entries.GetArrayElementAtIndex(index).FindPropertyRelative(SkillField);
            if (slot.FindPropertyRelative(ZonePrefabField).objectReferenceValue is not GameObject prefab) continue;
            if (!prefab.TryGetComponent(out PlayerGroundZoneEffect zone)) continue;
            set.Skills.Add(Build(zone, slot.FindPropertyRelative(ManaCostField).floatValue));
        }
        return set;
    }

    private static BalancePlayerSkill Build(PlayerGroundZoneEffect zone, float manaCost)
    {
        var skill = new BalancePlayerSkill
        {
            ManaCost = manaCost,
            Mode = zone.mode,
            Shape = zone.shape,
            Radius = zone.radius,
            Duration = zone.duration,
            TickInterval = zone.tickInterval,
            DamageAmount = zone.damageAmount,
            HealAmount = zone.healAmount,
            HpHealPer = zone.hpHealPer,
        };

        // Stat.Value: Buff 층 Additive는 합해서 (1+합)을 한 번 곱하고, Multiplier는 각각 곱한다. 시뮬은 ATK·AS만 옮긴다.
        float attackAdditive = 0f;
        float attackMultiplier = 1f;
        foreach (BuffEffect effect in zone.allyBuffs)
        {
            if (effect.statType == StatType.ATK && effect.modifierType == ModifierType.Additive) attackAdditive += effect.value;
            if (effect.statType == StatType.ATK && effect.modifierType == ModifierType.Multiplier) attackMultiplier *= 1f + effect.value;
            if (effect.statType == StatType.AS && effect.modifierType == ModifierType.Flat) skill.AttackSpeedAdd += effect.value;
        }
        skill.AttackScale = (1f + attackAdditive) * attackMultiplier;

        foreach (TargetDebuffRef reference in zone.targetDebuffs)
        {
            if (reference.debuff is not StatDebuffSO debuff || debuff.effects == null) continue;
            float scale = reference.scale > 0f ? reference.scale : 1f;
            foreach (DebuffStatEffect effect in debuff.effects)
            {
                if (effect.statType != StatType.SPD || effect.modifierType == ModifierType.Flat) continue;
                skill.MoveScale *= 1f + effect.value * scale;
            }
            skill.SlowLinger = reference.durationOverride > 0f ? reference.durationOverride : debuff.duration;
            skill.DebuffType |= debuff.type;
        }
        skill.MoveScale = Mathf.Max(0f, skill.MoveScale);

        skill.Label = skill.Mode switch
        {
            GroundZoneMode.Buff => BuffLabel,
            GroundZoneMode.Heal => HealLabel,
            _ => skill.Slows ? SlowLabel : DamageLabel,
        };
        return skill;
    }
}
