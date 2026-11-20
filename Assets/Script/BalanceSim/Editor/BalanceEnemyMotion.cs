using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

// 적 공격 모션 길이(초). EnemyBase.RunAttackLoop는 공격 모션 중에 다음 공격 타이머를 세지 않아서
// 실제 공격 주기 = 1/공속 + 모션 길이다. 적 프리팹의 attackClipLength가 0이면 배속 없이 Animator의
// "Attack" 상태 클립이 원래 길이대로 재생되므로 그 길이를 읽는다(0이 아니면 공격 간격까지 배속되어 그 값을 넘지 않는다).
public static class BalanceEnemyMotion
{
    private const string ResourcesPrefix = "Resources/";
    private const string AttackStateName = "Attack";

    private static readonly Dictionary<string, float> cache = new();

    // 공격 한 번에 걸리는 시간(타이머 + 모션). 프리팹·애니메이터를 못 찾으면 모션 0으로 본다.
    public static float AttackCycle(string prefabPath, float attackSpeed)
    {
        float interval = attackSpeed > 0f ? 1f / attackSpeed : 1f;
        float clip = ClipLength(prefabPath);
        return interval + Mathf.Min(clip, ClipCapped(prefabPath) ? interval : clip);
    }

    // 모션 중엔 제자리에 멈춘다(EnemyBase.Attack -> _move.Pause). 주기 중 움직이는 비율.
    public static float MovingShare(string prefabPath, float attackSpeed)
    {
        float interval = attackSpeed > 0f ? 1f / attackSpeed : 1f;
        return interval / AttackCycle(prefabPath, attackSpeed);
    }

    public static float ClipLength(string prefabPath)
    {
        if (string.IsNullOrEmpty(prefabPath)) return 0f;
        if (cache.TryGetValue(prefabPath, out float length)) return length;

        length = ReadClipLength(prefabPath);
        cache[prefabPath] = length;
        return length;
    }

    // attackClipLength가 0이 아니면 EnemyBase.Attack이 모션을 공격 간격에 맞춰 배속한다.
    private static bool ClipCapped(string prefabPath) => LoadEnemy(prefabPath) is EnemyBase enemy && SerializedClipLength(enemy) > 0f;

    private static float ReadClipLength(string prefabPath)
    {
        EnemyBase enemy = LoadEnemy(prefabPath);
        if (enemy == null) return 0f;
        Animator animator = enemy.GetComponentInChildren<Animator>(true);
        if (animator == null) return 0f;

        RuntimeAnimatorController runtime = animator.runtimeAnimatorController;
        var overrides = runtime as AnimatorOverrideController;
        if (overrides != null) runtime = overrides.runtimeAnimatorController;
        if (runtime is not AnimatorController controller || controller.layers.Length == 0) return 0f;

        foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
        {
            if (child.state.name != AttackStateName || child.state.motion is not AnimationClip clip) continue;
            AnimationClip played = overrides != null && overrides[clip] != null ? overrides[clip] : clip;
            float speed = Mathf.Approximately(child.state.speed, 0f) ? 1f : Mathf.Abs(child.state.speed);
            return played.length / speed;
        }
        return 0f;
    }

    private static float SerializedClipLength(EnemyBase enemy)
    {
        var serialized = new UnityEditor.SerializedObject(enemy);
        UnityEditor.SerializedProperty property = serialized.FindProperty("attackClipLength");
        return property != null ? property.floatValue : 0f;
    }

    // 웨이브 표의 Prefab 칸은 "Resources/EnemyUnitPrefabs/Chicken"처럼 Resources 기준 경로다.
    private static EnemyBase LoadEnemy(string prefabPath)
    {
        string path = prefabPath.StartsWith(ResourcesPrefix) ? prefabPath.Substring(ResourcesPrefix.Length) : prefabPath;
        GameObject prefab = Resources.Load<GameObject>(path);
        return prefab != null ? prefab.GetComponent<EnemyBase>() : null;
    }
}
