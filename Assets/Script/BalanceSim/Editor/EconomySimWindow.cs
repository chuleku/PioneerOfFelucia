using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// 낮 경제만 하루 단위로 돌려 보는 창(밸런스 시뮬 1단계). 입력을 받아 Runner에 맡기고, 결과를 표·그래프·CSV로 내보내기만 한다.
// 영웅·전투는 아직 없다 - 금은 영웅 생성·강화에만 쓰이므로 여기선 쌓이기만 하고, 영웅 몫 시민은 "영웅 예약"으로만 빼 둔다.
public class EconomySimWindow : EditorWindow
{
    private const string MenuPath = "Tools/Balance/Economy Simulator";
    private const string WindowTitle = "Economy Simulator";
    private const string ReloadLabel = "에셋 다시 읽기";
    private const string ExportLabel = "CSV 내보내기";
    private const string DaysLabel = "일수";
    private const string StrategyLabel = "전략";
    private const string HeroReserveLabel = "영웅 예약 시민";
    private const string TitleFoldoutLabel = "타이틀 강화 해금 단계";
    private const string ConstructDiscountLabel = "건설비 할인";
    private const string UpgradeDiscountLabel = "강화비 할인";
    private const string ProductAmountLabel = "생산량";
    private const string StartingResourceLabel = "시작 자원";
    private const string MaxCitizenLabel = "최대 시민";
    private const string CompareHeader = "전략 비교 (마지막 날 기준)";
    private const string ConditionNote =
        "전투·영웅 없음 · 금은 영웅 생성·강화에만 쓰여 이 단계에선 쌓이기만 함(영웅 예산으로 읽기) · 특수 자원(교역) 미반영 · " +
        "지역은 1번에서 시작해 10일마다 하나씩(11·21일차~) 열림 · 생산은 밤이 끝날 때 지급";
    private const string MissingAssetsNote =
        "경제 에셋을 찾지 못했습니다 — ProductionValue·HouseConfig·ProductionEconomyConfig·ResourcesManager/CitizenManager/BasePanel 프리팹을 확인하세요.";
    private const string CsvFileName = "economy_sim";
    private const string CsvExtension = "csv";

    private const int MinimumDays = 1;
    private const int MaximumDays = 200;
    private const int DefaultDays = 30;
    private const int MaximumHeroReserve = 50;
    private const float LabelWidth = 110f;
    private const float ChartHeight = 180f;
    private const float ChartPadding = 6f;
    private const float LegendWidth = 70f;

    private const float DayWidth = 40f;
    private const float ResourceWidth = 118f;
    private const float CountWidth = 132f;
    private const float CitizenWidth = 90f;

    private static readonly Color[] ResourceColors =
    {
        new Color(0.62f, 0.42f, 0.22f), // Wood
        new Color(0.45f, 0.75f, 0.30f), // Food
        new Color(0.95f, 0.78f, 0.20f), // Gold
        new Color(0.60f, 0.65f, 0.75f), // Iron
        new Color(0.55f, 0.55f, 0.55f), // Stone
    };
    private static readonly Color ChartBackground = new Color(0f, 0f, 0f, 0.2f);
    private static readonly string[] ResourceNames = Enum.GetNames(typeof(ProductionType));

    private EconomySimAssets assets;
    private readonly EconomySimTitleInput titleInput = new();
    private int dayCount = DefaultDays;
    private int heroReserve;
    private int strategyIndex;
    private bool titleFoldout;
    private string[] strategyNames = Array.Empty<string>();
    private List<EconomySimDayLog> logs = new();
    private readonly List<(string Name, EconomySimDayLog Last)> comparison = new();
    private Vector2 scroll;

    [MenuItem(MenuPath)]
    public static void OpenWindow()
    {
        GetWindow<EconomySimWindow>(WindowTitle);
    }

    private void OnEnable()
    {
        ReloadAssets();
    }

    private void ReloadAssets()
    {
        assets = EconomySimSource.Load();
        Rebuild();
    }

    private void OnGUI()
    {
        if (assets == null || !assets.IsValid)
        {
            EditorGUILayout.HelpBox(MissingAssetsNote, MessageType.Warning);
            if (GUILayout.Button(ReloadLabel)) ReloadAssets();
            return;
        }

        DrawControls();
        EditorGUILayout.LabelField(ConditionNote, EditorStyles.wordWrappedMiniLabel);
        DrawComparison();
        DrawChart();
        DrawTable();
    }

    // 입력이 바뀐 프레임에만 다시 돌린다. 200일 × 전략 3개도 순식간이라 버튼 없이 바로 갱신한다.
    private void DrawControls()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(ExportLabel, EditorStyles.toolbarButton)) ExportCsv();
            if (GUILayout.Button(ReloadLabel, EditorStyles.toolbarButton)) ReloadAssets();
        }

        EditorGUI.BeginChangeCheck();
        EditorGUIUtility.labelWidth = LabelWidth;
        dayCount = EditorGUILayout.IntSlider(DaysLabel, dayCount, MinimumDays, MaximumDays);
        strategyIndex = EditorGUILayout.Popup(StrategyLabel, strategyIndex, strategyNames);
        heroReserve = EditorGUILayout.IntSlider(HeroReserveLabel, heroReserve, 0, MaximumHeroReserve);

        titleFoldout = EditorGUILayout.Foldout(titleFoldout, TitleFoldoutLabel, true);
        if (titleFoldout)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                titleInput.ConstructDiscount = BranchSlider(ConstructDiscountLabel, titleInput.ConstructDiscount, assets.EconomyConfig.ConstructCostUpgrades.Count);
                titleInput.UpgradeDiscount = BranchSlider(UpgradeDiscountLabel, titleInput.UpgradeDiscount, assets.EconomyConfig.UpgradeCostUpgrades.Count);
                titleInput.ProductAmount = BranchSlider(ProductAmountLabel, titleInput.ProductAmount, assets.EconomyConfig.ProductAmountUpgrades.Count);
                titleInput.StartingResource = BranchSlider(StartingResourceLabel, titleInput.StartingResource, assets.StartingResourceUpgrades.Count);
                titleInput.MaxCitizen = BranchSlider(MaxCitizenLabel, titleInput.MaxCitizen, assets.MaxCitizenUpgrades.Count);
            }
        }

        if (EditorGUI.EndChangeCheck()) Rebuild();
    }

    private static int BranchSlider(string label, int value, int max)
    {
        return EditorGUILayout.IntSlider(label, Mathf.Min(value, max), 0, max);
    }

    // 모든 전략을 같은 조건으로 돌려 비교표를 만들고, 고른 전략의 일별 기록을 남긴다.
    private void Rebuild()
    {
        comparison.Clear();
        logs = new List<EconomySimDayLog>();
        if (assets == null || !assets.IsValid) return;

        List<IEconomySimStrategy> strategies = RuleEconomyStrategy.CreatePresets(heroReserve);
        strategyNames = strategies.ConvertAll(strategy => strategy.Name).ToArray();
        strategyIndex = Mathf.Clamp(strategyIndex, 0, strategies.Count - 1);

        for (int index = 0; index < strategies.Count; index++)
        {
            List<EconomySimDayLog> result = EconomySimRunner.Run(assets, titleInput, strategies[index], dayCount);
            comparison.Add((strategies[index].Name, result[result.Count - 1]));
            if (index == strategyIndex) logs = result;
        }
    }

    private void DrawComparison()
    {
        EditorGUILayout.LabelField(CompareHeader, EditorStyles.boldLabel);
        DrawHeaderRow(StrategyLabel, CountWidth);
        foreach (var (name, last) in comparison)
        {
            DrawLogRow(name, CountWidth, last);
        }
    }

    // 자원 5종 보유량 꺾은선. 축 눈금 대신 최댓값만 적는다.
    private void DrawChart()
    {
        if (logs.Count == 0) return;

        Rect area = GUILayoutUtility.GetRect(0f, ChartHeight, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;
        EditorGUI.DrawRect(area, ChartBackground);
        Rect plot = new Rect(area.x + ChartPadding, area.y + ChartPadding,
            area.width - LegendWidth - ChartPadding * 2f, area.height - ChartPadding * 2f);

        int max = 1;
        foreach (EconomySimDayLog log in logs)
        {
            foreach (int amount in log.DayEnd) max = Mathf.Max(max, amount);
        }

        for (int type = 0; type < ResourceNames.Length; type++)
        {
            var points = new Vector3[logs.Count];
            for (int index = 0; index < logs.Count; index++)
            {
                float x = logs.Count == 1 ? plot.x : plot.x + plot.width * index / (logs.Count - 1);
                float y = plot.yMax - plot.height * Mathf.Max(0, logs[index].DayEnd[type]) / max;
                points[index] = new Vector3(x, y);
            }
            Handles.color = ResourceColors[type % ResourceColors.Length];
            Handles.DrawAAPolyLine(2f, points);

            var legend = new Rect(plot.xMax + ChartPadding, plot.y + type * EditorGUIUtility.singleLineHeight, LegendWidth, EditorGUIUtility.singleLineHeight);
            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = ResourceColors[type % ResourceColors.Length] } };
            GUI.Label(legend, ResourceNames[type], style);
        }
        GUI.Label(new Rect(plot.x, plot.y, plot.width, EditorGUIUtility.singleLineHeight), $"max {max:N0}", EditorStyles.miniLabel);
    }

    private void DrawTable()
    {
        DrawHeaderRow(DaysLabel, DayWidth);
        using var scope = new EditorGUILayout.ScrollViewScope(scroll);
        scroll = scope.scrollPosition;
        foreach (EconomySimDayLog log in logs)
        {
            DrawLogRow(log.Day.ToString(), DayWidth, log);
        }
    }

    private static void DrawHeaderRow(string firstColumn, float firstWidth)
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label(firstColumn, GUILayout.Width(firstWidth));
            foreach (string name in ResourceNames)
            {
                GUILayout.Label($"{name} 낮 끝(+수입)", GUILayout.Width(ResourceWidth));
            }
            GUILayout.Label("시민 배치/현재/최대", GUILayout.Width(CountWidth));
            GUILayout.Label("시설·집 / 슬롯", GUILayout.Width(CountWidth));
            GUILayout.Label("행동", GUILayout.ExpandWidth(true));
        }
    }

    private static void DrawLogRow(string firstColumn, float firstWidth, EconomySimDayLog log)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(firstColumn, GUILayout.Width(firstWidth));
            for (int type = 0; type < ResourceNames.Length; type++)
            {
                GUILayout.Label($"{log.DayEnd[type]:N0} (+{log.Income[type]:N0})", GUILayout.Width(ResourceWidth));
            }
            GUILayout.Label($"{log.AssignedWorkers}/{log.CurrentCitizen}/{log.MaxCitizen}", GUILayout.Width(CountWidth));
            GUILayout.Label($"{FacilitySummary(log)}+{log.HouseCount} / {log.UsedSlots}/{log.TotalSlots}", GUILayout.Width(CountWidth));
            GUILayout.Label(ActionSummary(log), EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
        }
    }

    private static int FacilitySummary(EconomySimDayLog log)
    {
        int total = 0;
        foreach (int count in log.FacilityCounts) total += count;
        return total;
    }

    // 같은 행동이 연달아 나오면 "배치 4 ×3"처럼 묶는다.
    private static string ActionSummary(EconomySimDayLog log)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < log.Actions.Count; index++)
        {
            int repeat = 1;
            while (index + 1 < log.Actions.Count && log.Actions[index + 1] == log.Actions[index])
            {
                repeat++;
                index++;
            }
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(log.Actions[index]);
            if (repeat > 1) builder.Append($" ×{repeat}");
        }
        return builder.ToString();
    }

    // 엑셀에서 한글이 깨지지 않게 BOM 있는 UTF-8로 저장한다.
    private void ExportCsv()
    {
        if (logs.Count == 0) return;
        string path = EditorUtility.SaveFilePanel(ExportLabel, string.Empty, $"{CsvFileName}_{strategyNames[strategyIndex]}", CsvExtension);
        if (string.IsNullOrEmpty(path)) return;

        var builder = new StringBuilder();
        builder.Append("Day,Regions");
        foreach (string name in ResourceNames) builder.Append($",{name},{name}Income,{name}AfterPayout,{name}Spent,{name}Facilities");
        builder.AppendLine(",Houses,Citizens,MaxCitizens,Workers,OpenWorkerSlots,UsedSlots,TotalSlots,Actions");

        foreach (EconomySimDayLog log in logs)
        {
            builder.Append($"{log.Day},{log.UnlockedRegions}");
            for (int type = 0; type < ResourceNames.Length; type++)
            {
                builder.Append($",{log.DayEnd[type]},{log.Income[type]},{log.End[type]},{log.Spent[type]},{log.FacilityCounts[type]}");
            }
            builder.AppendLine($",{log.HouseCount},{log.CurrentCitizen},{log.MaxCitizen},{log.AssignedWorkers},{log.OpenWorkerSlots}," +
                $"{log.UsedSlots},{log.TotalSlots},\"{ActionSummary(log)}\"");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        EditorUtility.RevealInFinder(path);
    }
}
