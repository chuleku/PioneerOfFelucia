using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// "웨이브 확인" 접이식 패널. 고른 날의 지역별 적 구성·스탯과 지형 요약을 그대로 보여 준다(계산은 BalanceWavePlan).
// 인게임 스테이지 정보 툴팁(StageInfoView, 같은 EnemyStatScaling 사용)과 숫자를 맞춰 보는 용도다.
public static class BalanceWavePanel
{
    private const string FoldoutLabel = "웨이브 확인 (전투 입력값 점검)";
    private const string DayLabel = "확인할 일차";
    private const string MissingNoteFormat = "전투 값을 찾지 못했습니다 — {0}";
    private const float PanelHeight = 320f;
    private const float NameWidth = 120f;
    private const float NumberWidth = 64f;
    private const float WideWidth = 110f;

    public static void Draw(BalanceBattleAssets assets, ref bool foldout, ref int day, ref Vector2 scroll, int maxDay)
    {
        foldout = EditorGUILayout.Foldout(foldout, FoldoutLabel, true);
        if (!foldout) return;
        if (assets == null || !assets.IsValid)
        {
            EditorGUILayout.HelpBox(string.Format(MissingNoteFormat, assets?.Problem ?? "에셋을 아직 안 읽음"), MessageType.Warning);
            return;
        }

        day = EditorGUILayout.IntSlider(DayLabel, day, 1, maxDay);
        EditorGUILayout.LabelField($"본진 체력 {assets.BaseHp} · 해금 지역 {BalanceWavePlan.UnlockedRegionCount(assets, day)}개 " +
            "· 적이 본진에 닿으면 일반 -1 / 엘리트 -2 / 보스 즉시 패배", EditorStyles.miniLabel);

        using var scope = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.Height(PanelHeight));
        scroll = scope.scrollPosition;
        foreach (BalanceNightPlan plan in BalanceWavePlan.Build(assets, day))
        {
            DrawPlan(plan);
        }
    }

    private static void DrawPlan(BalanceNightPlan plan)
    {
        BalanceRegion region = plan.Region;
        string portal = plan.CornerPortal ? "보스 밤: 가장 먼 포탈 1개" : $"포탈 {plan.PortalCount}개";
        EditorGUILayout.LabelField(
            $"지역 {region.Region} (모듈 {region.ModuleId}) · 지역 {plan.LocalStage}일차 · 표 ID {plan.LookupId} · {portal}",
            EditorStyles.boldLabel);
        EditorGUILayout.LabelField(TerrainSummary(region), EditorStyles.miniLabel);

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("적", GUILayout.Width(NameWidth));
            GUILayout.Label("구분", GUILayout.Width(NumberWidth));
            GUILayout.Label("마릿수", GUILayout.Width(NumberWidth));
            GUILayout.Label("체력", GUILayout.Width(NumberWidth));
            GUILayout.Label("공격", GUILayout.Width(NumberWidth));
            GUILayout.Label("방어", GUILayout.Width(NumberWidth));
            GUILayout.Label("공속", GUILayout.Width(NumberWidth));
            GUILayout.Label("사거리", GUILayout.Width(NumberWidth));
            GUILayout.Label("칸/초", GUILayout.Width(NumberWidth));
            GUILayout.Label("등급·타입", GUILayout.Width(WideWidth));
            GUILayout.Label("출현(시작+간격)", GUILayout.Width(WideWidth));
            GUILayout.Label("특성", GUILayout.ExpandWidth(true));
        }
        foreach (BalanceSpawnGroup group in plan.Groups)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(group.Enemy.Name, GUILayout.Width(NameWidth));
                GUILayout.Label(group.Source, GUILayout.Width(NumberWidth));
                GUILayout.Label(group.Count.ToString(), GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.Hp:N0}", GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.Attack:N1}", GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.Defense:N1}", GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.Enemy.AttackSpeed:N2}", GUILayout.Width(NumberWidth));
                GUILayout.Label(group.Enemy.Range.ToString(), GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.TilesPerSecond:N2}", GUILayout.Width(NumberWidth));
                GUILayout.Label($"{group.Class}·{group.Type}", GUILayout.Width(WideWidth));
                GUILayout.Label($"{group.StartTime:N1}초 + {group.Delay:N1}초", GUILayout.Width(WideWidth));
                GUILayout.Label(group.Attribute.ToString(), GUILayout.ExpandWidth(true));
            }
        }
        EditorGUILayout.Space();
    }

    // 레인 수·길이, 영웅을 놓을 수 있는 칸 수, 공중 경로 수.
    private static string TerrainSummary(BalanceRegion region)
    {
        int shortest = int.MaxValue;
        int longest = 0;
        foreach (List<Vector2Int> lane in region.GroundLanes)
        {
            shortest = Mathf.Min(shortest, lane.Count);
            longest = Mathf.Max(longest, lane.Count);
        }
        int airRoutes = 0;
        foreach (List<List<Vector2Int>> routes in region.AirRoutes.Values) airRoutes += routes.Count;
        // 근접 영웅은 적이 지나가는 칸에 서야 막는다(BlockCalc). 이 수가 0이면 저지가 한 번도 일어나지 않는다.
        var laneTiles = new HashSet<Vector2Int>();
        foreach (List<Vector2Int> lane in region.GroundLanes) laneTiles.UnionWith(lane);
        int meleeOnLane = 0;
        foreach (Vector2Int tile in region.MeleeTiles)
        {
            if (laneTiles.Contains(tile)) meleeOnLane++;
        }

        string laneText = region.GroundLanes.Count > 0 ? $"지상 레인 {region.GroundLanes.Count}개({shortest}~{longest}칸)" : "지상 레인 없음";
        return $"{laneText} · 공중 경로 {airRoutes}개 · 근접 배치 칸 {region.MeleeTiles.Count}(레인 위 {meleeOnLane}) · 원거리 배치 칸 {region.RangedTiles.Count} " +
            $"· 칸 크기 {region.CellSize:N1} · 보스 지연 {region.BossSpawnDelay:N0}초";
    }
}
