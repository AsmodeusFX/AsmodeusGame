using IdleSword.SoundGen;

// 8bit 占位音频生成器。
//   dotnet run --project tools/SoundGen -- [输出目录]          生成全部素材
//   dotnet run --project tools/SoundGen -- [输出目录] --verify  只回读校验，不重新生成
// 生成后必须跑一次 `godot --headless --path idle-sword --import`，否则引擎侧没有 .import，
// 运行时 GD.Load 会拿到 null。
const double BeatSeconds = 60 / 150.0;
const double BeatFrames = BeatSeconds * Synth.Rate;

string output = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "idle-sword/Assets/Audio";
bool verifyOnly = args.Contains("--verify");

(string Id, Func<double[]> Build)[] sounds =
[
    ("bgm_battle", Sounds.Battle),
    ("sfx_cast_projectile", Sounds.CastProjectile),
    ("sfx_cast_target", Sounds.CastTarget),
    ("sfx_cast_ground", Sounds.CastGround),
    ("sfx_cast_buff", Sounds.CastBuff),
    ("sfx_cast_summon", Sounds.CastSummon),
    ("sfx_hit", Sounds.Hit),
    ("sfx_hit_heavy", Sounds.HitHeavy),
    ("sfx_kill", Sounds.Kill),
    ("sfx_click", Sounds.Click),
    ("sfx_panel", Sounds.Panel),
    ("sfx_buy", Sounds.Buy),
    ("sfx_deny", Sounds.Deny),
    ("sfx_reward", Sounds.Reward),
    ("sfx_breakthrough", Sounds.Breakthrough),
];

int failures = 0;
Console.WriteLine($"{(verifyOnly ? "校验" : "生成")}目录: {Path.GetFullPath(output)}");
Console.WriteLine($"{"素材",-24}{"时长",8}{"峰值",8}{"首",8}{"尾",8}  判定");
foreach (var (id, build) in sounds)
{
    string path = Path.Combine(output, id + ".wav");
    if (!verifyOnly) Synth.Write(path, build());

    string note = "";
    if (!File.Exists(path)) { note = "文件缺失"; }
    else
    {
        var info = Synth.Inspect(path);
        if (info.Peak < .05) note = "几乎无声";
        else if (info.Peak > .97) note = "削顶";
        // 循环曲的接缝必须连续：首尾样本差得越多，每圈回绕时的跳变越响。
        else if (id.StartsWith("bgm") && Math.Abs(info.Head - info.Tail) > .05) note = "接缝跳变";
        else if (id.StartsWith("bgm") && Math.Abs(info.Frames / BeatFrames - Math.Round(info.Frames / BeatFrames)) > 1e-6)
            note = "不是整数拍";
        if (note != "") failures++;
        Console.WriteLine($"{id,-24}{info.Frames / (double)Synth.Rate,7:0.00}s{info.Peak,8:0.00}{info.Head,8:0.000}{info.Tail,8:0.000}  {(note == "" ? "OK" : note)}");
        continue;
    }
    failures++;
    Console.WriteLine($"{id,-24}{"—",8}{"—",8}{"—",8}{"—",8}  {note}");
}

Console.WriteLine(failures == 0 ? $"全部 {sounds.Length} 个素材通过。" : $"{failures} 个素材有问题。");
return failures == 0 ? 0 : 1;
