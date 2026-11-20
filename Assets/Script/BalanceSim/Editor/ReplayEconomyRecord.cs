using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// 플레이 기록기(BalancePlayRecorder) 파일을 읽어 재현 봇으로 만든다. 낮 행동 줄만 기록 순서대로 따라 하고
// (밤 배치·스킬은 전투가 알아서 한다), 영웅은 기록에 나온 그 영웅으로 고정한다. 기록이 끝난 날부터는 fallback이 이어 받는다.
// 기록에 교환까지 다 적혀 있어 남은 펠리움으로 영웅을 더 뽑지 않는다. 시뮬 쪽 자원이 모자라 못 한 행동은 ✗로 남는다.
public partial class ReplayEconomyStrategy
{
    private const string RecordFolder = "Logs/PlayRecords";
    private const string RecordPattern = "*.txt";
    private const int MaxRecordPresets = 3;
    private const string RecordNameFormat = "기록 재현({0:MM-dd HH:mm}, 1~{1}일)";
    private const string MeleeWord = "근접";

    private static readonly Regex StartHeader = new(@"^── 시작 상태 \((\d+)일차");
    private static readonly Regex DayHeader = new(@"^══ (\d+)일차 낮 ══");
    private static readonly Regex NightHeader = new(@"^── \d+일차 밤 ──");
    private static readonly Regex RepeatSuffix = new(@"^(.*) ×(\d+)$");
    private static readonly Regex BuildLine = new(@"^건설 (.+) \(지역\d+-\d+\)$");
    private static readonly Regex ReplaceLine = new(@"^철거 (.+) → 건설 (.+) \(지역\d+-\d+\)$");
    private static readonly Regex DemolishLine = new(@"^철거 (.+) \(지역\d+-\d+\)$");
    private static readonly Regex UpgradeLine = new(@"^강화 (.+) → (\d+)단계(?: \(\+(\d+)\))? \(지역\d+-\d+\)$");
    private static readonly Regex RecruitLine = new(@"^시민 모집 \+(\d+)$");
    private static readonly Regex WorkerLine = new(@"^일꾼 (.+) \+(\d+)$");
    private static readonly Regex CreateLine = new(@"^(근접|원거리) 생성 → (.+)\(T(\d+)\)$");
    private static readonly Regex MergeLine = new(@"^합성 (.+?)\(T(\d+)\) \+ .* → (.+)\(T(\d+)\)$");
    private static readonly Regex ClassLine = new(@"^(근접|원거리) 직업 강화 → \d+(?: \(\+(\d+)\))?$");
    private static readonly Regex TradeLine = new(@"^펠리움→(\w+)$");

    // Logs/PlayRecords의 최근 기록 파일마다 재현 봇 하나(최대 3개, 최신 먼저).
    public static List<IEconomySimStrategy> FromRecords(Func<IEconomySimStrategy> fallback)
    {
        var strategies = new List<IEconomySimStrategy>();
        string folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, RecordFolder);
        if (!Directory.Exists(folder)) return strategies;

        var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles(RecordPattern));
        files.Sort((left, right) => right.LastWriteTime.CompareTo(left.LastWriteTime));
        foreach (FileInfo file in files)
        {
            if (strategies.Count >= MaxRecordPresets) break;
            Dictionary<int, List<ReplayStep>> script = ParseRecord(File.ReadAllLines(file.FullName));
            if (script.Count == 0) continue;
            int lastDay = 0;
            foreach (int day in script.Keys) lastDay = Mathf.Max(lastDay, day);
            strategies.Add(new ReplayEconomyStrategy(string.Format(RecordNameFormat, file.LastWriteTime, lastDay), script, fallback(), false));
        }
        return strategies;
    }

    // 낮 구간(시작 상태·n일차 낮 머리줄부터 밤 머리줄 전까지)의 행동 줄을 날마다 모은다.
    private static Dictionary<int, List<ReplayStep>> ParseRecord(string[] lines)
    {
        var script = new Dictionary<int, List<ReplayStep>>();
        List<ReplayStep> today = null;
        string pendingDemolish = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            Match header = StartHeader.Match(line);
            if (!header.Success) header = DayHeader.Match(line);
            if (header.Success)
            {
                today = new List<ReplayStep>();
                script[int.Parse(header.Groups[1].Value)] = today;
                pendingDemolish = null;
                continue;
            }
            if (NightHeader.IsMatch(line))
            {
                today = null;
                continue;
            }
            if (today == null) continue;

            int times = 1;
            Match repeat = RepeatSuffix.Match(line);
            if (repeat.Success)
            {
                line = repeat.Groups[1].Value;
                times = int.Parse(repeat.Groups[2].Value);
            }
            for (int count = 0; count < times; count++) AddRecordStep(today, line, ref pendingDemolish);
        }
        // 세이브를 불러와 중간부터 기록한 판은 그 전 상태가 없어 따라 할 수 없다.
        if (!script.ContainsKey(1)) script.Clear();
        return script;
    }

    private static void AddRecordStep(List<ReplayStep> today, string line, ref string pendingDemolish)
    {
        Match match;
        if ((match = ReplaceLine.Match(line)).Success)
        {
            today.AddRange(RecordReplace(match.Groups[1].Value, match.Groups[2].Value));
        }
        else if ((match = DemolishLine.Match(line)).Success)
        {
            // 시뮬은 철거만 따로 하지 않는다 - 바로 뒤 건설과 묶어 시설 바꾸기로 한다.
            pendingDemolish = match.Groups[1].Value;
        }
        else if ((match = BuildLine.Match(line)).Success)
        {
            today.AddRange(pendingDemolish != null ? RecordReplace(pendingDemolish, match.Groups[1].Value) : RecordBuild(match.Groups[1].Value));
            pendingDemolish = null;
        }
        else if ((match = UpgradeLine.Match(line)).Success)
        {
            int steps = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 1;
            today.AddRange(RecordUpgrade(match.Groups[1].Value, int.Parse(match.Groups[2].Value), steps));
        }
        else if ((match = RecruitLine.Match(line)).Success)
        {
            today.AddRange(Recruit(int.Parse(match.Groups[1].Value)));
        }
        else if ((match = WorkerLine.Match(line)).Success)
        {
            today.AddRange(RecordWorkers(match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
        }
        else if ((match = CreateLine.Match(line)).Success)
        {
            today.AddRange(RecordCreate(KindOf(match.Groups[1].Value), match.Groups[2].Value, int.Parse(match.Groups[3].Value)));
        }
        else if ((match = MergeLine.Match(line)).Success)
        {
            today.AddRange(RecordMerge(match.Groups[1].Value, int.Parse(match.Groups[2].Value),
                match.Groups[3].Value, int.Parse(match.Groups[4].Value)));
        }
        else if ((match = ClassLine.Match(line)).Success)
        {
            int steps = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            today.AddRange(ClassUp(KindOf(match.Groups[1].Value), steps));
        }
        else if ((match = TradeLine.Match(line)).Success && Enum.TryParse(match.Groups[1].Value, out ProductionType type))
        {
            today.AddRange(Trade(type));
        }
        // 자원·시민 요약, 펠리움 보상, 영웅 빠짐, 티어 강화(인게임에서 못 씀) 같은 줄은 따라 할 행동이 아니다.
    }

    private static OccupantKind KindOf(string word) => word == MeleeWord ? OccupantKind.MeleeHero : OccupantKind.RangedHero;

    // ── 기록 행동(이름은 세이브와 같은 건물 이름·영웅 이름) ──

    private static IEnumerable<ReplayStep> RecordBuild(string name) =>
        Repeat(1, $"{name} 건설", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.FreeSlots <= 0 ? "빈 슬롯 없음" : "자원 부족";
            if (world.IsHouseName(name)) return world.TryBuildHouse() ? $"{name} 건설" : null;
            if (!world.TryFindFacilityType(name, out ProductionType type))
            {
                reason = "모르는 건물";
                return null;
            }
            return world.TryBuildFacility(type) ? $"{name} 건설" : null;
        });

    // 철거는 건설비·강화비 전액 환불이라 시뮬도 같은 자리에서 바꾼다(EconomySimWorld.TryReplaceFacility). 강화가 가장 낮은 것을 바꾼다.
    private static IEnumerable<ReplayStep> RecordReplace(string oldName, string newName) =>
        Repeat(1, $"{oldName}→{newName} 바꾸기", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = "바꿀 시설 없음";
            if (!world.TryFindFacilityType(oldName, out ProductionType oldType) || !world.TryFindFacilityType(newName, out ProductionType newType)) return null;
            ProductionFacility target = null;
            foreach (ProductionFacility facility in world.Facilities)
            {
                if (facility.ProductionType != oldType) continue;
                if (target == null || facility.UpgradeCount < target.UpgradeCount) target = facility;
            }
            if (target == null) return null;
            reason = "자원 부족";
            return world.TryReplaceFacility(target, newType) ? $"{oldName}→{newName} 바꾸기" : null;
        });

    // "→ n단계": 그 종류 중 지금 n-1단계인 것을 먼저, 없으면 가장 낮은 것을 올린다(같은 종류가 여러 지역에 있다).
    private static IEnumerable<ReplayStep> RecordUpgrade(string name, int level, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            int from = level - steps + step;
            foreach (ReplayStep item in Repeat(1, $"{name} 강화", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
            {
                reason = "자원 부족";
                IUpgradableOccupant target = world.IsHouseName(name) ? PickHouse(world, from) : PickFacility(world, name, from);
                if (target == null)
                {
                    reason = "강화할 건물 없음";
                    return null;
                }
                return EconomySimWorld.TryUpgrade(target) ? $"{name} 강화" : null;
            }))
            {
                yield return item;
            }
        }
    }

    private static IUpgradableOccupant PickFacility(EconomySimWorld world, string name, int from)
    {
        if (!world.TryFindFacilityType(name, out ProductionType type)) return null;
        ProductionFacility pick = null;
        foreach (ProductionFacility facility in world.Facilities)
        {
            if (facility.ProductionType != type || facility.UpgradeCount >= facility.MaxUpgrade) continue;
            if (facility.UpgradeCount == from) return facility;
            if (pick == null || facility.UpgradeCount < pick.UpgradeCount) pick = facility;
        }
        return pick;
    }

    private static IUpgradableOccupant PickHouse(EconomySimWorld world, int from)
    {
        House pick = null;
        foreach (House house in world.Houses)
        {
            if (house.UpgradeCount == from) return house;
            if (pick == null || house.UpgradeCount < pick.UpgradeCount) pick = house;
        }
        return pick;
    }

    // 그 종류 시설 중 빈 일자리가 있는 곳에 한 명씩.
    private static IEnumerable<ReplayStep> RecordWorkers(string name, int count) =>
        Repeat(count, $"{name} 일꾼 배치", (EconomySimWorld world, BalanceHeroWorld _, EconomySimDayLog _, out string reason) =>
        {
            reason = world.Citizens.CanUseCitizen <= 0 ? "놀고 있는 시민 없음" : "빈 일자리 없음";
            if (world.Citizens.CanUseCitizen <= 0 || !world.TryFindFacilityType(name, out ProductionType type)) return null;
            foreach (ProductionFacility facility in world.Facilities)
            {
                if (facility.ProductionType == type && EconomySimWorld.AssignWorkers(facility, 1) == 1) return $"{name} 일꾼 배치";
            }
            return null;
        });

    private static IEnumerable<ReplayStep> RecordCreate(OccupantKind kind, string heroName, int tier) =>
        Repeat(1, $"{KindLabel(kind)} 생성({heroName})", (EconomySimWorld _, BalanceHeroWorld heroes, EconomySimDayLog _, out string reason) =>
        {
            HeroData data = heroes.FindHero(heroName, tier);
            reason = data == null ? "모르는 영웅" : heroes.GetBlockReason(kind) ?? "뽑기 실패";
            return data != null && heroes.TryCreate(kind, data, out BalanceHero created)
                ? $"{KindLabel(kind)} 생성 → {created.Data.HeroName}(T{created.Data.Tier})"
                : null;
        });

    private static IEnumerable<ReplayStep> RecordMerge(string fromName, int fromTier, string toName, int toTier) =>
        Repeat(1, $"합성 {fromName}(T{fromTier})", (EconomySimWorld _, BalanceHeroWorld heroes, EconomySimDayLog _, out string reason) =>
        {
            HeroData from = heroes.FindHero(fromName, fromTier);
            HeroData to = heroes.FindHero(toName, toTier);
            reason = from == null || to == null ? "모르는 영웅" : "같은 영웅 3명 없음";
            return from != null && to != null && heroes.TryMerge(from, to, out BalanceHero merged)
                ? $"합성 → {merged.Data.HeroName}(T{merged.Data.Tier})"
                : null;
        });
}
