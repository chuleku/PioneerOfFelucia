using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

// 플레이 기록기. 에디터 플레이 모드에서 낮마다 한 일(건설·강화·시민 모집·일꾼·영웅 생성·합성·직업 강화·펠리움 교환)과
// 밤마다 영웅 배치·플레이어 스킬·본진 피해를 텍스트 파일(프로젝트/Logs/PlayRecords)로 남긴다. 실플레이 재현 봇으로 옮기는 용도.
// 게임 코드는 건드리지 않는다: 세이브가 쓰는 SaveCapture로 상태를 읽고(조회만), 낮·밤 전환은 GameManager 이벤트를 구독한다
// (SaveManager와 같은 방식). 상태를 짧은 간격으로 읽어 앞 상태와 비교해 바뀐 것을 행동으로 적으므로 한 순서도 남는다.
[InitializeOnLoad]
public static class BalancePlayRecorder
{
    private const string MenuToggle = "Tools/Balance/플레이 기록";
    private const string MenuOpenFolder = "Tools/Balance/플레이 기록 폴더 열기";
    private const string EnabledPref = "BalancePlayRecorder.Enabled";
    private const string FolderName = "Logs/PlayRecords";
    private const string FileFormat = "플레이기록_{0:yyyyMMdd_HHmmss}.txt";
    private const double PollSeconds = 0.25;
    // 씬 시작 직후엔 세이브 불러오기가 상태를 한꺼번에 채운다 - 그게 끝난 뒤를 기준 상태로 잡는다.
    private const double AttachDelaySeconds = 2.0;
    private const int MeleeHeroType = 0;   // ClassSave.heroType / HeroData.HeroType: 0=근접, 1=원거리
    private const string BoardField = "board";

    private static GameManager game;
    private static SaveCapture capture;
    private static HeroRoster roster;
    private static CitizenManager citizens;
    private static ResourcesManager resources;
    private static PlayerManaManager mana;

    private static StreamWriter writer;
    private static SaveData last;
    private static readonly Dictionary<string, HeroData> heroById = new();
    private static readonly HashSet<int> seenZones = new();
    private static double attachAt;
    private static double nextPoll;
    private static bool night;
    private static float nightStart;
    private static int hpAtNightStart;
    private static bool gameOverWritten;
    private static int lastDay;   // 씬을 떠난 뒤 끝 줄에 쓸 일차(GameManager가 이미 사라졌을 수 있다)
    private static string pendingLine;
    private static int pendingCount;

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(EnabledPref, true);
        private set => EditorPrefs.SetBool(EnabledPref, value);
    }

    private static string Folder => Path.Combine(Directory.GetParent(Application.dataPath).FullName, FolderName);

    static BalancePlayRecorder()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        if (EditorApplication.isPlaying) Begin();
    }

    [MenuItem(MenuToggle)]
    private static void ToggleEnabled()
    {
        Enabled = !Enabled;
        if (!EditorApplication.isPlaying) return;
        if (Enabled) Begin();
        else End();
    }

    [MenuItem(MenuToggle, true)]
    private static bool ToggleEnabledValidate()
    {
        Menu.SetChecked(MenuToggle, Enabled);
        return true;
    }

    [MenuItem(MenuOpenFolder)]
    private static void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        EditorUtility.RevealInFinder(Folder);
    }

    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode) Begin();
        if (change == PlayModeStateChange.ExitingPlayMode) End();
    }

    private static void Begin()
    {
        if (!Enabled) return;
        attachAt = -1;
        EditorApplication.update -= Update;
        EditorApplication.update += Update;
    }

    private static void End()
    {
        EditorApplication.update -= Update;
        CloseSession(true);
    }

    // 지금 파일을 닫고 다음 게임(타이틀로 나갔다 다시 들어온 판)을 기다린다. 파일은 판마다 하나.
    private static void CloseSession(bool finalPoll)
    {
        if (game != null)
        {
            game.ChangeToDay -= OnDay;
            game.ChangeToNight -= OnNight;
        }
        if (writer != null)
        {
            if (finalPoll && game != null && last != null) Guard(Poll);
            Write($"── 기록 끝 ({lastDay}일차) ──");
            FlushPending();
            writer.Dispose();
            Debug.Log($"[플레이 기록] 저장함: {Folder}");
        }
        writer = null;
        game = null;
        last = null;
        heroById.Clear();
        seenZones.Clear();
        night = false;
        gameOverWritten = false;
        attachAt = -1;
    }

    // 기록기는 게임 진행에 절대 끼어들면 안 된다 - 낮·밤 전환 이벤트 안에서 예외가 나면 그 뒤 구독자(장판 정리 등)가
    // 안 불린다. 첫 예외에서 한 번 알리고 이번 판 기록을 멈춘다.
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[플레이 기록] 오류로 이번 판 기록을 멈춥니다: {exception}");
            if (writer != null) Write($"기록 중단: {exception.Message}");
            EditorApplication.update -= Update;
            try
            {
                CloseSession(false);
            }
            catch (Exception)
            {
                writer = null;
            }
        }
    }

    private static void Update() => Guard(Tick);

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        double now = EditorApplication.timeSinceStartup;
        if (writer != null && game == null)
        {
            CloseSession(false); // 게임 씬을 떠났다(타이틀로 나감)
            return;
        }
        if (writer == null)
        {
            WaitForGame(now);
            return;
        }
        if (now < nextPoll) return;
        nextPoll = now + PollSeconds;
        Poll();
    }

    // 게임 씬(GameLifeTimeScope)이 뜬 뒤 세이브 불러오기가 끝날 시간을 두고 붙는다(타이틀 화면에서 보낸 시간은 세지 않는다).
    private static void WaitForGame(double now)
    {
        var scope = Object.FindFirstObjectByType<GameLifeTimeScope>();
        if (scope == null || scope.Container == null)
        {
            attachAt = -1;
            return;
        }
        if (attachAt < 0) attachAt = now + AttachDelaySeconds;
        if (now >= attachAt) TryAttach(scope);
    }

    // 씬의 GameLifeTimeScope 컨테이너에서 세이브와 같은 객체들을 꺼낸다.
    private static void TryAttach(GameLifeTimeScope scope)
    {
        try
        {
            game = scope.Container.Resolve<GameManager>();
            capture = scope.Container.Resolve<SaveCapture>();
            roster = scope.Container.Resolve<HeroRoster>();
            citizens = scope.Container.Resolve<CitizenManager>();
            resources = scope.Container.Resolve<ResourcesManager>();
            mana = scope.Container.Resolve<PlayerManaManager>();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[플레이 기록] 게임 객체를 못 찾아 기록하지 않습니다: {exception.Message}");
            EditorApplication.update -= Update;
            return;
        }

        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, string.Format(FileFormat, DateTime.Now));
        writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
        game.ChangeToDay += OnDay;
        game.ChangeToNight += OnNight;

        RememberHeroes();
        last = Capture();
        Write($"# 플레이 기록 {DateTime.Now:yyyy-MM-dd HH:mm} · 시드 {game.GameSeed} · 교환량 {resources.TradeAmount}");
        Write($"── 시작 상태 ({game.DayCount}일차, {(game.CanBuild ? "낮" : "밤")}) ──");
        WriteState(last);
        WriteHeroes(last);
        night = !game.CanBuild;
        if (night) StartNight();
    }

    // ── 낮·밤 전환 ──

    private static void OnNight() => Guard(WriteNightStart);

    private static void WriteNightStart()
    {
        if (writer == null) return;
        Poll();
        Write($"── {game.DayCount}일차 밤 ──");
        StartNight();
        WritePlacement(last);
    }

    private static void StartNight()
    {
        night = true;
        nightStart = Time.time;
        hpAtNightStart = game.Hp;
        seenZones.Clear();
        foreach (PlayerGroundZoneEffect zone in Object.FindObjectsByType<PlayerGroundZoneEffect>(FindObjectsSortMode.None)) seenZones.Add(zone.GetInstanceID());
    }

    private static void OnDay() => Guard(WriteDayStart);

    private static void WriteDayStart()
    {
        if (writer == null) return;
        Poll();
        if (night) Write($"밤 결과: 본진 {hpAtNightStart} → {game.Hp} ({Time.time - nightStart:N0}초)");
        night = false;
        Write($"══ {game.DayCount}일차 낮 ══");
        WriteState(last);
    }

    // ── 상태 비교 ──

    private static SaveData Capture() => capture.CaptureSaveData(SavePhase.DayActive, game.DayCount, 0f, Array.Empty<int>());

    private static void Poll()
    {
        RememberHeroes();
        SaveData now = Capture();
        lastDay = game.DayCount;
        DiffResources(last, now);
        DiffBuilds(last, now);
        DiffHeroes(last, now);
        DiffLevels(last, now);
        if (night)
        {
            DiffPlacement(last, now);
            FindSkills();
        }
        if (game.isGameOver && !gameOverWritten)
        {
            gameOverWritten = true;
            Write($"게임 오버 ({game.DayCount}일차, 본진 {game.Hp})");
        }
        last = now;
    }

    // 펠리움이 줄면서 어떤 자원이 교환량만큼 늘었으면 교환. 늘어난 펠리움은 보상(무피해·보스).
    private static void DiffResources(SaveData before, SaveData after)
    {
        int citizensAdded = after.currentCitizen - before.currentCitizen;
        if (citizensAdded > 0) Write($"시민 모집 +{citizensAdded}");

        int felium = after.specialAmount - before.specialAmount;
        if (felium > 0) Write($"펠리움 +{felium} (보상)");
        if (felium >= 0) return;

        bool matched = false;
        foreach (var (type, delta) in ResourceDeltas(before, after))
        {
            int trades = Mathf.RoundToInt((float)delta / resources.TradeAmount);
            if (delta <= 0 || trades <= 0) continue;
            Write(trades > 1 ? $"펠리움→{type} ×{trades}" : $"펠리움→{type}");
            matched = true;
        }
        if (!matched) Write($"펠리움 {felium} (교환 자원 모름)");
    }

    private static IEnumerable<(ProductionType Type, int Delta)> ResourceDeltas(SaveData before, SaveData after)
    {
        yield return (ProductionType.Wood, after.woodAmount - before.woodAmount);
        yield return (ProductionType.Stone, after.stoneAmount - before.stoneAmount);
        yield return (ProductionType.Iron, after.ironAmount - before.ironAmount);
        yield return (ProductionType.Gold, after.goldAmount - before.goldAmount);
        yield return (ProductionType.Food, after.foodAmount - before.foodAmount);
    }

    private static void DiffBuilds(SaveData before, SaveData after)
    {
        var old = new Dictionary<(int, int), BuildSave>();
        foreach (BuildSave build in before.buildList) old[(build.moduleId, build.slotIndex)] = build;

        foreach (BuildSave build in after.buildList)
        {
            string where = $"지역{build.moduleId}-{build.slotIndex}";
            if (!old.Remove((build.moduleId, build.slotIndex), out BuildSave previous))
            {
                Write($"건설 {build.buildKey} ({where})");
                continue;
            }
            if (previous.buildKey != build.buildKey) Write($"철거 {previous.buildKey} → 건설 {build.buildKey} ({where})");
            if (build.upgradeCount > previous.upgradeCount) Write($"강화 {build.buildKey} → {build.upgradeCount}단계{Steps(build.upgradeCount - previous.upgradeCount)} ({where})");
            int workers = build.workerAmount - previous.workerAmount;
            if (workers != 0) Write($"일꾼 {build.buildKey} {workers:+0;-0}");
        }
        foreach (BuildSave removed in old.Values) Write($"철거 {removed.buildKey} (지역{removed.moduleId}-{removed.slotIndex})");
    }

    // 같은 순간에 영웅이 빠지고 새로 생기면 합성, 생기기만 하면 생성.
    private static void DiffHeroes(SaveData before, SaveData after)
    {
        var oldIds = new HashSet<string>();
        foreach (HeroSave hero in before.heroList) oldIds.Add(hero.rosterId);
        var added = new List<string>();
        foreach (HeroSave hero in after.heroList)
        {
            if (!oldIds.Remove(hero.rosterId)) added.Add(hero.rosterId);
        }
        if (added.Count == 0 && oldIds.Count == 0) return;

        if (added.Count > 0 && oldIds.Count > 0)
        {
            var consumed = new List<string>();
            foreach (string id in oldIds) consumed.Add(HeroLabel(id));
            foreach (string id in added) Write($"합성 {string.Join(" + ", consumed)} → {HeroLabel(id)}");
            return;
        }
        foreach (string id in added) Write($"{KindLabel(id)} 생성 → {HeroLabel(id)}");
        foreach (string id in oldIds) Write($"영웅 빠짐 {HeroLabel(id)}");
    }

    private static void DiffLevels(SaveData before, SaveData after)
    {
        var classes = new Dictionary<int, int>();
        foreach (ClassSave level in before.classList) classes[level.heroType] = level.classLevel;
        foreach (ClassSave level in after.classList)
        {
            classes.TryGetValue(level.heroType, out int previous);
            if (level.classLevel > previous) Write($"{(level.heroType == MeleeHeroType ? "근접" : "원거리")} 직업 강화 → {level.classLevel}{Steps(level.classLevel - previous)}");
        }

        var tiers = new Dictionary<int, int>();
        foreach (TierSave level in before.tierList) tiers[level.heroTier] = level.tierLevel;
        foreach (TierSave level in after.tierList)
        {
            tiers.TryGetValue(level.heroTier, out int previous);
            if (level.tierLevel > previous) Write($"T{level.heroTier} 티어 강화 → {level.tierLevel}{Steps(level.tierLevel - previous)}");
        }
    }

    // 밤 중 재배치(옮김·회수·새로 놓음). 낮의 배치는 밤 시작 때 한 번에 적는다.
    private static void DiffPlacement(SaveData before, SaveData after)
    {
        var old = new Dictionary<string, PlaceSave>();
        foreach (PlaceSave place in before.placeList) old[place.rosterId] = place;
        foreach (PlaceSave place in after.placeList)
        {
            if (old.Remove(place.rosterId, out PlaceSave previous) && previous.moduleId == place.moduleId && previous.cellOrigin == place.cellOrigin) continue;
            Write($"[밤 {NightSeconds():N1}초] 배치 {HeroLabel(place.rosterId)} → 지역{place.moduleId} {Cell(place.cellOrigin)}");
        }
        foreach (PlaceSave removed in old.Values) Write($"[밤 {NightSeconds():N1}초] 회수 {HeroLabel(removed.rosterId)}");
    }

    // 새로 깔린 플레이어 장판 = 스킬 사용. 칸은 장판이 들고 있는 보드로 바꾼다.
    private static void FindSkills()
    {
        foreach (PlayerGroundZoneEffect zone in Object.FindObjectsByType<PlayerGroundZoneEffect>(FindObjectsSortMode.None))
        {
            if (!seenZones.Add(zone.GetInstanceID())) continue;
            string where = string.Empty;
            if (typeof(PlayerGroundZoneEffect).GetField(BoardField, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zone) is MapBoard board && board != null)
            {
                where = $" → 지역{board.Module.ModuleId} {Cell(board.WorldToCell(zone.transform.position))}";
            }
            Write($"[밤 {NightSeconds():N1}초] 스킬 {SkillLabel(zone)}{where} · 남은 마나 {mana.CurrentMana:N0}");
        }
    }

    private static string SkillLabel(PlayerGroundZoneEffect zone) => zone.mode switch
    {
        GroundZoneMode.Buff => "공격 버프",
        GroundZoneMode.Heal => "힐",
        _ => zone.targetDebuffs.Count > 0 ? "둔화" : "피해 장판",
    };

    // ── 출력 ──

    private static void WriteState(SaveData state)
    {
        int houses = 0;
        int facilities = 0;
        foreach (BuildSave build in state.buildList)
        {
            if (build.buildKind == OccupantKind.Building) houses++;
            else facilities++;
        }
        Write($"자원 목재 {state.woodAmount} · 석재 {state.stoneAmount} · 철 {state.ironAmount} · 금 {state.goldAmount} · 식량 {state.foodAmount} · 펠리움 {state.specialAmount}");
        Write($"시민 {state.currentCitizen}/{citizens.MaxCitizen} (일꾼 {state.usedCitizen} · 영웅 {state.heroUsedCitizen}) · 시설 {facilities} · 집 {houses} · 영웅 {state.heroList.Length} · 본진 {game.Hp}");
    }

    private static void WriteHeroes(SaveData state)
    {
        foreach (BuildSave build in state.buildList)
        {
            Write($"  시설 {build.buildKey} {build.upgradeCount}단계 · 일꾼 {build.workerAmount} (지역{build.moduleId}-{build.slotIndex})");
        }
        foreach (HeroSave hero in state.heroList) Write($"  영웅 {HeroLabel(hero.rosterId)}");
        foreach (ClassSave level in state.classList) Write($"  {(level.heroType == MeleeHeroType ? "근접" : "원거리")} 직업 {level.classLevel}단계");
    }

    private static void WritePlacement(SaveData state)
    {
        var placed = new HashSet<string>();
        var byModule = new SortedDictionary<int, List<string>>();
        foreach (PlaceSave place in state.placeList)
        {
            placed.Add(place.rosterId);
            if (!byModule.TryGetValue(place.moduleId, out List<string> list)) byModule[place.moduleId] = list = new List<string>();
            list.Add($"{HeroLabel(place.rosterId)}{Cell(place.cellOrigin)}");
        }
        foreach (var (module, list) in byModule) Write($"배치 지역{module}: {string.Join(", ", list)}");

        var waiting = new List<string>();
        foreach (HeroSave hero in state.heroList)
        {
            if (!placed.Contains(hero.rosterId)) waiting.Add(HeroLabel(hero.rosterId));
        }
        if (waiting.Count > 0) Write($"대기(배치 안 함): {string.Join(", ", waiting)}");
    }

    private static void RememberHeroes()
    {
        foreach (HeroRosterEntry entry in roster.Entries) heroById[entry.Id.ToString()] = entry.Data;
    }

    private static string HeroLabel(string rosterId) =>
        heroById.TryGetValue(rosterId, out HeroData data) && data != null ? $"{data.HeroName}(T{data.Tier})" : "영웅(?)";

    private static string KindLabel(string rosterId) =>
        heroById.TryGetValue(rosterId, out HeroData data) && data != null && data.HeroType != MeleeHeroType ? "원거리" : "근접";

    // 한 번 읽는 사이에 여러 단계가 올랐으면 몇 단계였는지 붙인다.
    private static string Steps(int steps) => steps > 1 ? $" (+{steps})" : string.Empty;

    private static string Cell(Vector2Int cell) => $"({cell.x},{cell.y})";

    private static float NightSeconds() => Time.time - nightStart;

    // 같은 줄이 이어지면 "줄 ×n"으로 묶는다(일꾼 한 명씩 배치 등).
    private static void Write(string line)
    {
        if (line == pendingLine)
        {
            pendingCount++;
            return;
        }
        FlushPending();
        pendingLine = line;
        pendingCount = 1;
    }

    private static void FlushPending()
    {
        if (pendingLine == null || writer == null) return;
        writer.WriteLine(pendingCount > 1 ? $"{pendingLine} ×{pendingCount}" : pendingLine);
        pendingLine = null;
        pendingCount = 0;
    }
}
