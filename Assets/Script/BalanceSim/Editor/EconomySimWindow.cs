using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// 밸런스 시뮬 창. 1단계(낮 경제) + 2단계(영웅 생성·합성·강화). 입력을 받아 Runner에 맡기고, 결과를 표·그래프·CSV로 내보내기만 한다.
// 전투·배치는 3단계 - 전투력은 "보유 영웅 합계"(배치 칸 수·시너지 미반영)다.
public class EconomySimWindow : EditorWindow
{
    private const string MenuPath = "Tools/Balance/Economy Simulator";
    private const string WindowTitle = "Economy Simulator";
    private const string RunLabel = "실행";
    private const string ReloadLabel = "에셋 다시 읽기";
    private const string ExportLabel = "CSV 내보내기";
    private const string DaysLabel = "일수";
    private const string StrategyLabel = "전략";
    private const string SeedLabel = "시드";
    private const string SeedCountLabel = "반복(시드 수)";
    private const string ChartModeLabel = "그래프";
    private const string UseBattleLabel = "전투 사용(끄면 가정 모드)";
    private const string BaseHpTitleLabel = "본진 체력";
    private const string HeroDamageLabel = "영웅 피해 보정(%)";
    private const string HeroHpLabel = "영웅 체력 보정(%)";
    private const string BattleDetailHeader = "전투 상세 (누수가 있었거나 보스가 나온 밤)";
    private const int MinimumTuningPercent = 10;
    private const int MaximumTuningPercent = 500;
    private const float DetailHeight = 110f;
    private const string PerfectNightLabel = "무피해 밤 비율(%)";
    private const string KillBossLabel = "보스 처치(10일마다 +5)";
    private const string TitleFoldoutLabel = "타이틀 강화 해금 단계";
    private const string ConstructDiscountLabel = "건설비 할인";
    private const string UpgradeDiscountLabel = "강화비 할인";
    private const string ProductAmountLabel = "생산량";
    private const string StartingResourceLabel = "시작 자원";
    private const string MaxCitizenLabel = "최대 시민";
    private const string HeroCreateDiscountLabel = "영웅 생성비 할인";
    private const string HeroUpgradeDiscountLabel = "영웅 강화비 할인";
    private const string DirtyNote = "입력이 바뀌었습니다 — 실행을 누르면 반영됩니다.";
    private const string CompareHeaderFormat = "전략 비교 ({0}일차, 시드 {1}개 평균) · 계산 {2:N1}초";
    private const string ConditionNote =
        "전투: 밤마다 영웅을 자동 배치하고 0.25초 단위로 이동·저지·공격·누수를 계산(스킬·버프·장판·힐 미반영) · 전투를 끄면 펠리움은 가정값 · 전투력 = 보유 영웅 합계(방어 0) · 특수 자원(교역) 미반영 · " +
        "지역은 1번에서 시작해 10일마다 하나씩(11·21일차~) 열림 · 생산은 밤이 끝날 때 지급 · 자원 칸은 낮 끝 보유량(+다음 날 수입)";
    private const string MissingAssetsNote =
        "경제 에셋을 찾지 못했습니다 — ProductionValue·HouseConfig·ProductionEconomyConfig·ResourcesManager/CitizenManager/BasePanel 프리팹을 확인하세요.";
    private const string MissingHeroNote =
        "영웅 값을 찾지 못했습니다 — MainScene의 HeroSetPanel·HeroCreateManager·HeroRegistry와 HeroUpgradeConfig·HeroClassUpgradeConfig를 확인하세요.";
    private const string CsvFileName = "balance_sim";
    private const string CsvExtension = "csv";
    private const string DefaultSeed = "balance";
    private const string NoBattleMark = "-";

    private const int MinimumDays = 1;
    private const int MaximumDays = 200;
    private const int DefaultDays = 30;
    private const int MaximumSeedCount = 50;
    private const int DefaultSeedCount = 10;
    private const float LabelWidth = 120f;
    private const float ChartHeight = 180f;
    private const float ChartPadding = 6f;
    private const float LegendWidth = 70f;

    private const float DayWidth = 40f;
    private const float StrategyWidth = 120f;
    private const float ResourceWidth = 104f;
    private const float CitizenWidth = 112f;
    private const float FacilityWidth = 92f;
    private const float HeroWidth = 150f;
    private const float PowerWidth = 120f;
    private const float BlockWidth = 130f;
    private const float FeliumWidth = 90f;
    private const float SurvivalWidth = 110f;
    private const float BattleWidth = 230f;
    private const float CompareNumberWidth = 150f;

    private static readonly Color[] ResourceColors =
    {
        new Color(0.62f, 0.42f, 0.22f), // Wood
        new Color(0.45f, 0.75f, 0.30f), // Food
        new Color(0.95f, 0.78f, 0.20f), // Gold
        new Color(0.60f, 0.65f, 0.75f), // Iron
        new Color(0.55f, 0.55f, 0.55f), // Stone
    };
    private static readonly Color DpsColor = new Color(0.90f, 0.35f, 0.30f);
    private static readonly Color HpColor = new Color(0.35f, 0.65f, 0.95f);
    private static readonly Color ChartBackground = new Color(0f, 0f, 0f, 0.2f);
    private static readonly string[] ResourceNames = Enum.GetNames(typeof(ProductionType));
    private static readonly string[] ChartModes = { "자원", "전투력" };

    // 전략 하나를 시드 여러 개로 돌린 마지막 날 요약.
    private class StrategySummary
    {
        public string Name;
        public float HeroCount;
        public float DpsMean;
        public float DpsMin = float.MaxValue;
        public float DpsMax;
        public float HpMean;
        public float GoldMean;
        public int FacilityCount;
        public float FeliumUsed;   // 한 판 동안 교환에 쓴 펠리움(시드 평균)
        public float SurvivedDays;  // 마지막으로 버틴 일차(시드 평균)
        public int Defeats;         // 패배한 판 수
        public int Runs;
        public bool HasBattle;      // 전투를 켜고 돌렸는지(끄면 생존·무피해·본진 칸은 "-")
        public float PerfectNights;
        public float BossKills;
        public float FinalBaseHp;
        public readonly Dictionary<string, int> BlockDays = new(); // 막힌 이유별 일수(시드 전체 합)
    }

    private EconomySimAssets assets;
    private BalanceHeroAssets heroAssets;
    private BalanceBattleAssets battleAssets;
    private readonly EconomySimTitleInput titleInput = new();
    private readonly EconomySimFeliumInput feliumInput = new();
    private readonly Dictionary<(HeroData, int), HeroSimResult> powerCache = new();
    private int dayCount = DefaultDays;
    private int strategyIndex;
    private string seed = DefaultSeed;
    private int seedCount = DefaultSeedCount;
    private int chartMode;
    private bool titleFoldout;
    private bool waveFoldout;
    private int waveDay = 1;
    private Vector2 waveScroll;
    private bool dirty;
    private float lastRunSeconds;
    private int heroDamagePercent = 100;
    private int heroHpPercent = 100;
    private Vector2 detailScroll;
    private string[] strategyNames = Array.Empty<string>();
    private string poolNote = string.Empty;
    private List<EconomySimDayLog> logs = new();
    private readonly List<StrategySummary> comparison = new();
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
        heroAssets = BalanceHeroSource.CreateWithAssets();
        battleAssets = BalanceBattleSource.CreateWithTables();
        heroAssets.SourceNote = BalanceSceneReader.Read(scene =>
        {
            BalanceHeroSource.ReadScene(scene, heroAssets);
            BalanceBattleSource.ReadScene(scene, battleAssets);
        });
        powerCache.Clear();
        poolNote = BuildPoolNote();
        // 전략 비교(전략×시드 전부)는 전투가 들어가 무거워서 실행 버튼으로만 돌린다 - 스크립트가 다시 컴파일될 때마다
        // 에디터가 멈추지 않게, 여기선 고른 전략 한 판만 보여 준다.
        comparison.Clear();
        RebuildDisplayed();
        dirty = true;
    }

    private bool AssetsReady => assets != null && assets.IsValid && heroAssets != null && heroAssets.IsValid;

    private void OnGUI()
    {
        if (!AssetsReady)
        {
            if (assets == null || !assets.IsValid) EditorGUILayout.HelpBox(MissingAssetsNote, MessageType.Warning);
            if (heroAssets == null || !heroAssets.IsValid) EditorGUILayout.HelpBox(MissingHeroNote, MessageType.Warning);
            if (GUILayout.Button(ReloadLabel)) ReloadAssets();
            return;
        }

        DrawControls();
        EditorGUILayout.LabelField(ConditionNote, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.LabelField(poolNote, EditorStyles.wordWrappedMiniLabel);
        if (dirty) EditorGUILayout.HelpBox(DirtyNote, MessageType.Info);
        BalanceWavePanel.Draw(battleAssets, ref waveFoldout, ref waveDay, ref waveScroll, MaximumDays);
        DrawComparison();
        DrawChart();
        DrawBattleDetails();
        DrawTable();
    }

    private void DrawControls()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button(RunLabel, EditorStyles.toolbarButton)) Rebuild();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(ExportLabel, EditorStyles.toolbarButton)) ExportCsv();
            if (GUILayout.Button(ReloadLabel, EditorStyles.toolbarButton)) ReloadAssets();
        }

        EditorGUIUtility.labelWidth = LabelWidth;
        EditorGUI.BeginChangeCheck();
        dayCount = EditorGUILayout.IntSlider(DaysLabel, dayCount, MinimumDays, MaximumDays);
        seed = EditorGUILayout.TextField(SeedLabel, seed);
        seedCount = EditorGUILayout.IntSlider(SeedCountLabel, seedCount, 1, MaximumSeedCount);
        feliumInput.UseBattle = EditorGUILayout.Toggle(UseBattleLabel, feliumInput.UseBattle);
        // 보정은 전투를 켤 때만 쓴다(스킬·버프 등 시뮬에 없는 영웅 능력을 실제 플레이에 맞춰 메운다).
        using (new EditorGUI.DisabledScope(!feliumInput.UseBattle))
        {
            heroDamagePercent = EditorGUILayout.IntSlider(HeroDamageLabel, heroDamagePercent, MinimumTuningPercent, MaximumTuningPercent);
            heroHpPercent = EditorGUILayout.IntSlider(HeroHpLabel, heroHpPercent, MinimumTuningPercent, MaximumTuningPercent);
        }
        // 가정 모드 입력은 전투를 끌 때만 쓴다.
        using (new EditorGUI.DisabledScope(feliumInput.UseBattle))
        {
            feliumInput.PerfectNightPercent = EditorGUILayout.IntSlider(PerfectNightLabel, feliumInput.PerfectNightPercent, 0, 100);
            feliumInput.KillBoss = EditorGUILayout.Toggle(KillBossLabel, feliumInput.KillBoss);
        }

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
                titleInput.HeroCreateDiscount = BranchSlider(HeroCreateDiscountLabel, titleInput.HeroCreateDiscount, heroAssets.CreateCostUpgrades.Count);
                titleInput.HeroUpgradeDiscount = BranchSlider(HeroUpgradeDiscountLabel, titleInput.HeroUpgradeDiscount, heroAssets.ClassConfig.UpgradeCostUpgrades.Count);
                titleInput.BaseHp = BranchSlider(BaseHpTitleLabel, titleInput.BaseHp, battleAssets?.BaseHpUpgrades.Count ?? 0);
            }
        }
        if (EditorGUI.EndChangeCheck()) dirty = true;

        // 표시할 전략만 바꾸는 건 그 전략 한 판만 다시 돌리면 되니 바로 반영한다.
        EditorGUI.BeginChangeCheck();
        strategyIndex = EditorGUILayout.Popup(StrategyLabel, strategyIndex, strategyNames);
        if (EditorGUI.EndChangeCheck()) RebuildDisplayed();
        chartMode = EditorGUILayout.Popup(ChartModeLabel, chartMode, ChartModes);
    }

    private static int BranchSlider(string label, int value, int max)
    {
        return EditorGUILayout.IntSlider(label, Mathf.Min(value, max), 0, max);
    }

    // 모든 전략을 시드 seedCount개로 돌려 비교표를 만들고, 고른 전략의 첫 시드 결과를 일별 표로 남긴다.
    private void Rebuild()
    {
        comparison.Clear();
        logs = new List<EconomySimDayLog>();
        dirty = false;
        if (!AssetsReady) return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ApplyTuning();
        List<IEconomySimStrategy> strategies = RuleEconomyStrategy.CreatePresets();
        strategyNames = strategies.ConvertAll(strategy => strategy.Name).ToArray();

        int totalRuns = strategies.Count * seedCount;
        try
        {
            for (int index = 0; index < strategies.Count; index++)
            {
                var summary = new StrategySummary { Name = strategies[index].Name };
                int finished = 0;
                for (int run = 0; run < seedCount; run++)
                {
                    float progress = (float)(index * seedCount + run) / totalRuns;
                    if (EditorUtility.DisplayCancelableProgressBar(WindowTitle, $"{summary.Name} · 시드 {run + 1}/{seedCount}", progress)) break;
                    List<EconomySimDayLog> result = EconomySimRunner.Run(assets, heroAssets, titleInput, feliumInput, battleAssets,
                        strategies[index], dayCount, RunSeed(run), false, powerCache);
                    Accumulate(summary, result);
                    finished++;
                }
                if (finished == 0) break;
                Finish(summary, finished);
                comparison.Add(summary);
                if (finished < seedCount) break; // 취소 - 여기까지 끝난 전략만 남긴다
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        RebuildDisplayed();
        lastRunSeconds = (float)stopwatch.Elapsed.TotalSeconds;
    }

    // 표·그래프에 올릴 한 판(고른 전략, 첫 시드)은 매일 전투력까지 계산해 다시 돌린다.
    private void RebuildDisplayed()
    {
        if (!AssetsReady) return;
        ApplyTuning();
        List<IEconomySimStrategy> strategies = RuleEconomyStrategy.CreatePresets();
        strategyNames = strategies.ConvertAll(strategy => strategy.Name).ToArray();
        strategyIndex = Mathf.Clamp(strategyIndex, 0, strategies.Count - 1);
        logs = EconomySimRunner.Run(assets, heroAssets, titleInput, feliumInput, battleAssets, strategies[strategyIndex], dayCount, RunSeed(0), true, powerCache);
    }

    // 보정값은 창이 들고 있고(다시 읽어도 유지), 돌리기 직전에 전투 값 묶음에 넣는다.
    private void ApplyTuning()
    {
        if (battleAssets == null) return;
        battleAssets.HeroDamagePercent = heroDamagePercent;
        battleAssets.HeroHpPercent = heroHpPercent;
    }

    private string RunSeed(int run) => $"{seed}-{run}";

    private static void Accumulate(StrategySummary summary, List<EconomySimDayLog> result)
    {
        EconomySimDayLog last = result[result.Count - 1];
        summary.HeroCount += last.HeroCount;
        summary.DpsMean += last.PowerDps;
        summary.DpsMin = Mathf.Min(summary.DpsMin, last.PowerDps);
        summary.DpsMax = Mathf.Max(summary.DpsMax, last.PowerDps);
        summary.HpMean += last.PowerHp;
        summary.GoldMean += last.DayEnd[(int)ProductionType.Gold];
        foreach (int count in last.FacilityCounts) summary.FacilityCount += count;
        summary.SurvivedDays += last.Day;
        summary.HasBattle = last.HasBattle;
        summary.FinalBaseHp += last.BaseHp;
        if (last.GameOver) summary.Defeats++;

        foreach (EconomySimDayLog log in result)
        {
            summary.FeliumUsed += log.FeliumUsed;
            if (log.PerfectNight) summary.PerfectNights++;
            if (log.BossKilled) summary.BossKills++;
            if (log.HeroBlock == null) continue;
            summary.BlockDays.TryGetValue(log.HeroBlock, out int days);
            summary.BlockDays[log.HeroBlock] = days + 1;
        }
    }

    // runs판으로 나눠 평균을 낸다(취소하면 seedCount보다 적을 수 있다).
    private static void Finish(StrategySummary summary, int runs)
    {
        summary.Runs = runs;
        summary.HeroCount /= runs;
        summary.DpsMean /= runs;
        summary.HpMean /= runs;
        summary.GoldMean /= runs;
        summary.FacilityCount /= runs;
        summary.FeliumUsed /= runs;
        summary.SurvivedDays /= runs;
        summary.FinalBaseHp /= runs;
        summary.PerfectNights /= runs;
        summary.BossKills /= runs;
    }

    // (티어, 종류)별 생성·합성 후보 수. 같은 영웅 3개가 모여야 합성되므로 후보가 많을수록 합성이 드물다.
    private string BuildPoolNote()
    {
        if (heroAssets == null || !heroAssets.IsValid) return string.Empty;
        var builder = new StringBuilder($"영웅 값: {heroAssets.SourceNote} · 후보 수");
        foreach (var (ranged, label) in new[] { (false, "근접"), (true, "원거리") })
        {
            builder.Append($" · {label}");
            for (int tier = 1; tier <= EconomySimRunner.MaxHeroTier; tier++)
            {
                int count = 0;
                foreach (HeroData data in heroAssets.Pool)
                {
                    if (data.Tier == tier && (data.HeroType == BalanceHero.RangedHeroType) == ranged) count++;
                }
                builder.Append($" T{tier}:{count}");
            }
        }
        builder.Append($" · 생성가 점증 하루 {heroAssets.DailyIncreaseRate:P0} / 지역 {heroAssets.RegionIncreaseRate:P0}");
        return builder.ToString();
    }

    private void DrawComparison()
    {
        if (comparison.Count == 0) return;
        EditorGUILayout.LabelField(string.Format(CompareHeaderFormat, dayCount, comparison[0].Runs, lastRunSeconds), EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label(StrategyLabel, GUILayout.Width(StrategyWidth));
            GUILayout.Label("영웅 수", GUILayout.Width(FacilityWidth));
            GUILayout.Label("초당 피해 평균(최소~최대)", GUILayout.Width(CompareNumberWidth + FacilityWidth));
            GUILayout.Label("체력 합계 평균", GUILayout.Width(CompareNumberWidth));
            GUILayout.Label("금 낮 끝", GUILayout.Width(FacilityWidth));
            GUILayout.Label("시설", GUILayout.Width(BlockWidth));
            GUILayout.Label("펠리움 사용", GUILayout.Width(FeliumWidth));
            GUILayout.Label("생존일(패배 판)", GUILayout.Width(SurvivalWidth));
            GUILayout.Label("무피해 밤", GUILayout.Width(FeliumWidth));
            GUILayout.Label("보스 처치", GUILayout.Width(FeliumWidth));
            GUILayout.Label("본진 체력", GUILayout.Width(FeliumWidth));
            GUILayout.Label("영웅 생성이 막힌 날(이유별, 시드 합)", GUILayout.ExpandWidth(true));
        }
        foreach (StrategySummary summary in comparison)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(summary.Name, GUILayout.Width(StrategyWidth));
                GUILayout.Label($"{summary.HeroCount:N1}", GUILayout.Width(FacilityWidth));
                GUILayout.Label($"{summary.DpsMean:N1} ({summary.DpsMin:N1}~{summary.DpsMax:N1})", GUILayout.Width(CompareNumberWidth + FacilityWidth));
                GUILayout.Label($"{summary.HpMean:N0}", GUILayout.Width(CompareNumberWidth));
                GUILayout.Label($"{summary.GoldMean:N0}", GUILayout.Width(FacilityWidth));
                GUILayout.Label($"{summary.FacilityCount}", GUILayout.Width(BlockWidth));
                GUILayout.Label($"{summary.FeliumUsed:N1}", GUILayout.Width(FeliumWidth));
                GUILayout.Label(summary.HasBattle ? $"{summary.SurvivedDays:N1} ({summary.Defeats}/{summary.Runs})" : NoBattleMark, GUILayout.Width(SurvivalWidth));
                GUILayout.Label(summary.HasBattle ? $"{summary.PerfectNights:N1}" : NoBattleMark, GUILayout.Width(FeliumWidth));
                GUILayout.Label(summary.HasBattle ? $"{summary.BossKills:N1}" : NoBattleMark, GUILayout.Width(FeliumWidth));
                GUILayout.Label(summary.HasBattle ? $"{summary.FinalBaseHp:N1}" : NoBattleMark, GUILayout.Width(FeliumWidth));
                GUILayout.Label(BlockSummary(summary.BlockDays), EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            }
        }
    }

    private static string BlockSummary(Dictionary<string, int> blockDays)
    {
        if (blockDays.Count == 0) return "-";
        var entries = new List<KeyValuePair<string, int>>(blockDays);
        entries.Sort((left, right) => right.Value.CompareTo(left.Value));
        return string.Join(" · ", entries.ConvertAll(entry => $"{entry.Key} {entry.Value}"));
    }

    // 자원 5종 보유량 또는 전투력(초당 피해·체력) 꺾은선. 전투력은 둘의 단위가 달라 각자 최댓값 기준으로 그린다.
    private void DrawChart()
    {
        if (logs.Count == 0) return;

        Rect area = GUILayoutUtility.GetRect(0f, ChartHeight, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;
        EditorGUI.DrawRect(area, ChartBackground);
        Rect plot = new Rect(area.x + ChartPadding, area.y + ChartPadding,
            area.width - LegendWidth - ChartPadding * 2f, area.height - ChartPadding * 2f);

        if (chartMode == 0)
        {
            float max = 1f;
            foreach (EconomySimDayLog log in logs)
            {
                foreach (int amount in log.DayEnd) max = Mathf.Max(max, amount);
            }
            for (int type = 0; type < ResourceNames.Length; type++)
            {
                int captured = type;
                DrawLine(plot, log => log.DayEnd[captured], max, ResourceColors[type % ResourceColors.Length], ResourceNames[type], type);
            }
            DrawMaxLabel(plot, $"max {max:N0}");
            return;
        }

        float maxDps = 1f;
        float maxHp = 1f;
        foreach (EconomySimDayLog log in logs)
        {
            maxDps = Mathf.Max(maxDps, log.PowerDps);
            maxHp = Mathf.Max(maxHp, log.PowerHp);
        }
        DrawLine(plot, log => log.PowerDps, maxDps, DpsColor, "초당 피해", 0);
        DrawLine(plot, log => log.PowerHp, maxHp, HpColor, "체력", 1);
        DrawMaxLabel(plot, $"피해 max {maxDps:N1} · 체력 max {maxHp:N0}");
    }

    private void DrawLine(Rect plot, Func<EconomySimDayLog, float> value, float max, Color color, string legend, int legendRow)
    {
        var points = new Vector3[logs.Count];
        for (int index = 0; index < logs.Count; index++)
        {
            float x = logs.Count == 1 ? plot.x : plot.x + plot.width * index / (logs.Count - 1);
            float y = plot.yMax - plot.height * Mathf.Max(0f, value(logs[index])) / max;
            points[index] = new Vector3(x, y);
        }
        Handles.color = color;
        Handles.DrawAAPolyLine(2f, points);

        var rect = new Rect(plot.xMax + ChartPadding, plot.y + legendRow * EditorGUIUtility.singleLineHeight, LegendWidth, EditorGUIUtility.singleLineHeight);
        var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = color } };
        GUI.Label(rect, legend, style);
    }

    // 표 칸에 다 안 들어가는 전투 요약(지역별 누수·배치, 보스 상세)을 문제가 있던 밤만 줄줄이 보여 준다.
    private void DrawBattleDetails()
    {
        var lines = new List<string>();
        foreach (EconomySimDayLog log in logs)
        {
            if (log.HasBattle && (log.EnemyLeaked > 0 || log.BattleNote.Contains("보스"))) lines.Add($"{log.Day}일차: {log.BattleNote}");
        }
        if (lines.Count == 0) return;

        EditorGUILayout.LabelField(BattleDetailHeader, EditorStyles.boldLabel);
        using var scope = new EditorGUILayout.ScrollViewScope(detailScroll, GUILayout.Height(DetailHeight));
        detailScroll = scope.scrollPosition;
        foreach (string line in lines) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
    }

    private static void DrawMaxLabel(Rect plot, string text)
    {
        GUI.Label(new Rect(plot.x, plot.y, plot.width, EditorGUIUtility.singleLineHeight), text, EditorStyles.miniLabel);
    }

    private void DrawTable()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label(DaysLabel, GUILayout.Width(DayWidth));
            foreach (string name in ResourceNames)
            {
                GUILayout.Label($"{name}(+수입)", GUILayout.Width(ResourceWidth));
            }
            GUILayout.Label("시민 일/영웅/현재/최대", GUILayout.Width(CitizenWidth));
            GUILayout.Label("시설+집 / 슬롯", GUILayout.Width(FacilityWidth));
            GUILayout.Label("영웅 근/원 · T1~T4", GUILayout.Width(HeroWidth));
            GUILayout.Label("피해 / 체력", GUILayout.Width(PowerWidth));
            GUILayout.Label("펠리움 보유(+획득/-사용)", GUILayout.Width(FeliumWidth + 40f));
            GUILayout.Label("본진(-피해) · 누수/등장 · 배치+인벤", GUILayout.Width(BattleWidth));
            GUILayout.Label("막힘", GUILayout.Width(BlockWidth));
            GUILayout.Label("행동", GUILayout.ExpandWidth(true));
        }

        using var scope = new EditorGUILayout.ScrollViewScope(scroll);
        scroll = scope.scrollPosition;
        foreach (EconomySimDayLog log in logs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(log.Day.ToString(), GUILayout.Width(DayWidth));
                for (int type = 0; type < ResourceNames.Length; type++)
                {
                    GUILayout.Label($"{log.DayEnd[type]:N0} (+{log.Income[type]:N0})", GUILayout.Width(ResourceWidth));
                }
                GUILayout.Label($"{log.AssignedWorkers}/{log.HeroCitizens}/{log.CurrentCitizen}/{log.MaxCitizen}", GUILayout.Width(CitizenWidth));
                GUILayout.Label($"{FacilityTotal(log)}+{log.HouseCount} / {log.UsedSlots}/{log.TotalSlots}", GUILayout.Width(FacilityWidth));
                GUILayout.Label($"{log.MeleeCount}/{log.RangedCount} · {TierSummary(log)}", GUILayout.Width(HeroWidth));
                GUILayout.Label($"{log.PowerDps:N1} / {log.PowerHp:N0}", GUILayout.Width(PowerWidth));
                GUILayout.Label($"{log.FeliumEnd} (+{log.FeliumGained}/-{log.FeliumUsed})", GUILayout.Width(FeliumWidth + 40f));
                GUILayout.Label(BattleCell(log), GUILayout.Width(BattleWidth));
                GUILayout.Label(log.HeroBlock ?? "-", GUILayout.Width(BlockWidth));
                string detail = log.HasBattle ? $"[{log.BattleNote}] {ActionSummary(log)}" : ActionSummary(log);
                GUILayout.Label(new GUIContent(detail, detail), EditorStyles.miniLabel, GUILayout.ExpandWidth(true)); // 잘리면 마우스를 올려 전체를 본다
            }
        }
    }

    // 전투 칸: 본진 체력(이 밤 피해) · 누수/등장 · 배치+인벤토리. 패배·보스 처치는 따로 표시.
    private static string BattleCell(EconomySimDayLog log)
    {
        if (!log.HasBattle) return "-";
        string flags = (log.GameOver ? " 패배" : string.Empty) + (log.BossKilled ? " 보스처치" : string.Empty);
        return $"{log.BaseHp}(-{log.BaseDamage}) · {log.EnemyLeaked}/{log.EnemySpawned} · {log.Placed}+{log.Inventory}{flags}";
    }

    private static int FacilityTotal(EconomySimDayLog log)
    {
        int total = 0;
        foreach (int count in log.FacilityCounts) total += count;
        return total;
    }

    private static string TierSummary(EconomySimDayLog log)
    {
        var parts = new string[EconomySimRunner.MaxHeroTier];
        for (int tier = 1; tier <= EconomySimRunner.MaxHeroTier; tier++)
        {
            parts[tier - 1] = log.HeroTierCounts[tier].ToString();
        }
        return string.Join("/", parts);
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
        builder.Append(",Houses,Citizens,MaxCitizens,Workers,HeroCitizens,OpenWorkerSlots,UsedSlots,TotalSlots,Melee,Ranged");
        for (int tier = 1; tier <= EconomySimRunner.MaxHeroTier; tier++) builder.Append($",T{tier}");
        builder.AppendLine(",CreatedToday,MeleeClassLv,RangedClassLv,Dps,Hp,Felium,FeliumGained,FeliumUsed,BaseHp,BaseDamage,EnemyLeaked,EnemySpawned,Perfect,BossKilled,Placed,Inventory,HeroDeaths,GameOver,HeroBlock,Actions");

        foreach (EconomySimDayLog log in logs)
        {
            builder.Append($"{log.Day},{log.UnlockedRegions}");
            for (int type = 0; type < ResourceNames.Length; type++)
            {
                builder.Append($",{log.DayEnd[type]},{log.Income[type]},{log.End[type]},{log.Spent[type]},{log.FacilityCounts[type]}");
            }
            builder.Append($",{log.HouseCount},{log.CurrentCitizen},{log.MaxCitizen},{log.AssignedWorkers},{log.HeroCitizens}," +
                $"{log.OpenWorkerSlots},{log.UsedSlots},{log.TotalSlots},{log.MeleeCount},{log.RangedCount}");
            for (int tier = 1; tier <= EconomySimRunner.MaxHeroTier; tier++) builder.Append($",{log.HeroTierCounts[tier]}");
            builder.AppendLine($",{log.CreatedToday},{log.MeleeClassLevel},{log.RangedClassLevel},{log.PowerDps:F1},{log.PowerHp:F0},{log.FeliumEnd},{log.FeliumGained},{log.FeliumUsed},{log.BaseHp},{log.BaseDamage},{log.EnemyLeaked},{log.EnemySpawned}," +
                $"{log.PerfectNight},{log.BossKilled},{log.Placed},{log.Inventory},{log.HeroDeaths},{log.GameOver}," +
                $"{log.HeroBlock},\"{ActionSummary(log)}\"");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        EditorUtility.RevealInFinder(path);
    }
}
