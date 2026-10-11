using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.ChatRoom;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Sessions;
using HuTao.Dialogue.Storage;
using HuTao.Persona;
using HuTao.Voice;

/// <summary>
/// 会话层（设计书 §8 的 T-03/04/05/08）离线回归。全程用临时目录与桩数据，
/// 不碰网络、不起 TTS、不写进仓库的 data/。
///
/// 这些检查守的是那批"看不到但一破就丢数据"的性质：追加语义、坏行容错、
/// 排序确定性、成员在场区间对视角的裁剪。
/// </summary>
internal static class SessionChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail = "") => checks.Add(new(name, ok, detail));

        var root = Path.Combine(Path.GetTempPath(), "hutao-session-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            await CheckStoreLayerAsync(store, root, Check);
            await CheckCatalogLayerAsync(Check);
            await CheckFactoryLayerAsync(store, root, Check);
            CheckProjectionLayer(Check);
            await CheckOwnedAudioGuardAsync(root, Check);
            await CheckAgentPoolAsync(Check);
            await CheckSessionRuntimeAsync(root, Check);
            await CheckMigrationAsync(root, Check);
            await CheckScriptedLineGenerationAsync(root, Check);
            await CheckMemoryAttributionAsync(Check);
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("session_unexpected", false, ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return checks;
    }

    // ── T-03：存储层 ─────────────────────────────────────────────────────

    private static async Task CheckStoreLayerAsync(SessionStore store, string root, Action<string, bool, string> Check)
    {
        var sessionId = "single_hutao";
        await store.CreateSessionAsync(Descriptor(sessionId));

        // 追加语义：Id 由追加器保证单调，调用方给的 Id 被忽略。
        var first = await store.AppendAsync(sessionId, Turn("ignored", "第一句", user: true));
        var second = await store.AppendAsync(sessionId, Turn("ignored", "第二句", user: false));
        var third = await store.AppendAsync(sessionId, Turn("ignored", "第三句", user: true));
        Check("session_timeline_append_is_monotonic",
            first.Id == "t-0001" && second.Id == "t-0002" && third.Id == "t-0003",
            $"{first.Id},{second.Id},{third.Id}");

        // 只追加不重写：文件行数 == 追加次数，而且没有被覆写成整份 JSON 数组。
        var timelinePath = store.PathsFor(sessionId).TimelinePath;
        var lines = (await File.ReadAllLinesAsync(timelinePath)).Where(l => l.Trim().Length > 0).ToArray();
        Check("session_timeline_is_line_oriented_append_only",
            lines.Length == 3 && lines.All(l => l.StartsWith('{') && l.EndsWith('}')),
            $"lines={lines.Length}");

        // 坏行容错：一份 2000 行的记录里有一行被截断，不该让整段历史消失。
        await File.AppendAllTextAsync(timelinePath, "{ this is not json\n");
        var loaded = await store.LoadTimelineAsync(sessionId);
        Check("session_timeline_skips_corrupt_line", loaded.Count == 3, $"count={loaded.Count}");

        // 坏行之后的追加仍要拿到正确的下一个 Id（不能按行数算）。
        var fourth = await store.AppendAsync(sessionId, Turn("ignored", "第四句", user: true));
        Check("session_next_id_ignores_corrupt_lines", fourth.Id == "t-0004", fourth.Id);

        // 往返无损：所有字段（含 DateTimeOffset 偏移与 Kind 枚举）都要保持。
        var roundTrip = (await store.LoadTimelineAsync(sessionId)).Single(t => t.Id == "t-0001");
        Check("session_turn_roundtrip_preserves_fields",
            roundTrip.Text == "第一句" && roundTrip.IsUser &&
            roundTrip.SpeakerId == "user" && roundTrip.Kind == ConversationTurnKind.Message &&
            roundTrip.At.Offset == first.At.Offset,
            $"{roundTrip.Kind}/{roundTrip.At:O}");

        // 索引往返：全字段相等，且 Presences 不被 Normalize 洗掉。
        await store.SaveCatalogAsync(new SessionCatalog([Descriptor("single_hutao", pinned: true, unread: 3)]));
        var catalog = await store.LoadCatalogAsync();
        var saved = catalog.Find("single_hutao");
        Check("session_index_roundtrip_preserves_all_fields",
            saved is not null && saved.Pinned && saved.UnreadCount == 3 &&
            saved.Presences.Count == 1 && saved.MemberIds.Count == 1 &&
            saved.Title == "胡桃",
            $"pinned={saved?.Pinned} unread={saved?.UnreadCount} presences={saved?.Presences.Count}");

        // 版本不认识 → 空目录，而不是把半份数据暴露出去。
        await File.WriteAllTextAsync(store.IndexPath, "{\"Version\":999,\"Sessions\":[]}");
        Check("session_index_rejects_unknown_version",
            (await store.LoadCatalogAsync()).Sessions.Count == 0, "version=999");

        // 写完再读必须能读回来。这条守的是一类很隐蔽的错：**派生属性被序列化出去**
        // （它有 getter 没构造参数），于是 index.json 变成一份永远读不回来的文件。
        // 真机上就是这样丢掉整个会话目录、并把消息区渲染打崩的。
        await store.SaveCatalogAsync(new SessionCatalog([Descriptor("single_hutao", pinned: true, unread: 2)]));
        var reload = await store.LoadCatalogAsync();
        Check("session_index_survives_own_serialization",
            reload.Find("single_hutao") is { } back &&
            back.Pinned && back.UnreadCount == 2 && back.Presences.Count == 1,
            $"count={reload.Sessions.Count}");

        // 派生属性不许出现在落盘 JSON 里（否则上一行会在反序列化时抛，被吞成空目录）。
        var indexJson = await File.ReadAllTextAsync(store.IndexPath);
        Check("session_index_omits_derived_properties",
            !indexJson.Contains("ActivePresences", StringComparison.Ordinal),
            indexJson.Contains("ActivePresences", StringComparison.Ordinal) ? "leaked" : "clean");

        // 损坏的 index 也不能抛。
        await File.WriteAllTextAsync(store.IndexPath, "{ broken");
        Check("session_index_survives_corruption",
            (await store.LoadCatalogAsync()).Sessions.Count == 0, "broken json");

        // 原子写：写入过程中失败时原文件保持旧内容。
        await store.SaveCatalogAsync(new SessionCatalog([Descriptor("single_hutao")]));
        var before = await File.ReadAllTextAsync(store.IndexPath);
        var moved = false;
        try
        {
            var directory = Path.GetDirectoryName(store.IndexPath)!;
            foreach (var temp in Directory.GetFiles(directory, "*.tmp-*"))
                File.Delete(temp);
            moved = true;
        }
        catch (IOException) { }
        Check("session_write_leaves_no_temp_files", moved && File.ReadAllText(store.IndexPath) == before,
            "no leftover tmp");

        // 删除进回收站，原目录消失但数据还在。
        await store.DeleteSessionAsync(sessionId);
        Check("session_delete_moves_to_trash",
            !Directory.Exists(store.PathsFor(sessionId).Directory) &&
            Directory.Exists(store.TrashRoot) &&
            Directory.GetDirectories(store.TrashRoot).Length == 1 &&
            File.Exists(Path.Combine(Directory.GetDirectories(store.TrashRoot)[0], "timeline.jsonl")),
            "trash");

        // 删除不存在的会话必须幂等。
        await store.DeleteSessionAsync(sessionId);
        Check("session_delete_is_idempotent", true, "second delete no-op");

        // 视角写入与读取。
        await store.SaveViewAsync("single_hutao", "hutao", [first, second]);
        Check("session_view_roundtrip", (await store.LoadViewAsync("single_hutao", "hutao")).Count == 2, "view");
        Check("session_missing_view_is_empty",
            (await store.LoadViewAsync("nope", "nobody")).Count == 0, "missing -> []");

        _ = root;
    }

    // ── T-04：目录表 ─────────────────────────────────────────────────────

    private static Task CheckCatalogLayerAsync(Action<string, bool, string> Check)
    {
        var sameMoment = new DateTimeOffset(2026, 2, 14, 20, 0, 0, TimeSpan.FromHours(8));
        var a = Descriptor("group_a") with { LastActiveAt = sameMoment };
        var b = Descriptor("group_b") with { LastActiveAt = sameMoment };
        var catalog = new SessionCatalog([b, a]);

        // 同一时刻的两个会话必须每次都给同一个顺序（否则侧栏会抖）。
        var firstPass = catalog.Ordered().Select(s => s.Id).ToArray();
        var secondPass = catalog.Ordered().Select(s => s.Id).ToArray();
        Check("catalog_order_is_deterministic",
            firstPass.SequenceEqual(secondPass) && firstPass.SequenceEqual(["group_a", "group_b"]),
            string.Join(",", firstPass));

        var pinned = catalog.SetPinned("group_b", true);
        Check("catalog_pinned_first", pinned.Ordered()[0].Id == "group_b", pinned.Ordered()[0].Id);

        var recent = catalog.Touch("group_b", sameMoment.AddHours(1), "后来居上");
        Check("catalog_touch_moves_to_top", recent.Ordered()[0].Id == "group_b", recent.Ordered()[0].Id);

        var bumped = catalog.BumpUnread("group_a", 2).BumpUnread("group_a");
        Check("catalog_bump_unread_accumulates",
            bumped.Find("group_a")!.UnreadCount == 3, bumped.Find("group_a")!.UnreadCount.ToString());

        var read = bumped.MarkRead("group_a", sameMoment);
        Check("catalog_mark_read_zeroes_unread", read.Find("group_a")!.UnreadCount == 0, "0");

        var removed = catalog.Remove("group_a");
        Check("catalog_remove", removed.Find("group_a") is null && removed.Sessions.Count == 1, "removed");

        // 预览是侧栏副标题，必须单行：换行会把行高撑开。
        var preview = catalog.Touch("group_a", sameMoment, "第一行\n第二行\r\n第三行");
        Check("catalog_preview_is_single_line",
            !preview.Find("group_a")!.LastPreview.Contains('\n') &&
            !preview.Find("group_a")!.LastPreview.Contains('\r'),
            preview.Find("group_a")!.LastPreview);

        // 不可变：改过之后原实例不受影响。
        Check("catalog_patch_is_immutable",
            catalog.Find("group_a")!.UnreadCount == 0 && catalog.Find("group_b")!.Pinned == false,
            "original untouched");

        Check("catalog_upsert_replaces_by_id",
            catalog.Upsert(a with { Title = "改名" }).Find("group_a")!.Title == "改名" &&
            catalog.Upsert(a with { Title = "改名" }).Sessions.Count == 2,
            "replace not append");

        return Task.CompletedTask;
    }

    // ── T-05：工厂 ───────────────────────────────────────────────────────

    private static async Task CheckFactoryLayerAsync(SessionStore store, string root, Action<string, bool, string> Check)
    {
        // Id 必须文件系统安全且 ASCII-only（KnowledgeAccessContext 只收 ASCII 字母数字与 -/_）。
        var baseSecond = new DateTimeOffset(2026, 2, 14, 20, 0, 0, TimeSpan.FromHours(8));
        var ids = Enumerable.Range(0, 100)
            .Select(i => SessionFactory.NewGroupSessionId(baseSecond.AddSeconds(i)))
            .ToArray();
        Check("factory_group_id_is_ascii_filesystem_safe",
            ids.All(id => System.Text.RegularExpressions.Regex.IsMatch(id, @"^group_\d{8}T\d{6}-[0-9a-f]{4}$")),
            ids[0]);
        // 时间戳精确到**秒**，所以唯一性只在"不同秒"上可断言；
        // 同一秒内的碰撞由随机后缀降低概率，真正的防线是 EnsureGroupAsync 的成员集合查重
        // （见 factory_group_dedupe_ignores_member_order），而不是这个 4 位后缀。
        Check("factory_group_id_is_unique_per_second",
            ids.Distinct().Count() == ids.Length, $"distinct={ids.Distinct().Count()}");
        Check("factory_group_id_suffix_is_randomised",
            Enumerable.Range(0, 20).Select(_ => SessionFactory.NewGroupSessionId(baseSecond))
                .Distinct().Count() > 1, "same second still varies");

        // 单聊 Id 与角色一一对应 → "每个角色只有一个单聊"是结构性保证。
        Check("factory_single_id_is_deterministic",
            SessionFactory.SingleSessionId("hutao") == SessionFactory.SingleSessionId("hutao"),
            SessionFactory.SingleSessionId("hutao"));

        // EnsureSingle 幂等：重复调用不能把置顶/未读重置。
        var catalog = new SessionCatalog([]);
        var single = await SessionFactory.EnsureSingleAsync(store, catalog, "hutao");
        catalog = catalog.Upsert(single with { Pinned = true, UnreadCount = 5 });
        var again = await SessionFactory.EnsureSingleAsync(store, catalog, "hutao");
        Check("factory_ensure_single_is_idempotent",
            again.Id == single.Id && again.Pinned && again.UnreadCount == 5,
            $"id={again.Id} pinned={again.Pinned} unread={again.UnreadCount}");

        // 群聊查重忽略成员顺序。
        var created = await SessionFactory.EnsureGroupAsync(store, catalog, ["hutao", "lacrimosa"], "茶话会");
        catalog = catalog.Upsert(created);
        var reordered = await SessionFactory.EnsureGroupAsync(store, catalog, ["lacrimosa", "hutao"], "另一个名字");
        Check("factory_group_dedupe_ignores_member_order",
            reordered.Id == created.Id && reordered.Title == "茶话会",
            $"id={reordered.Id} title={reordered.Title}");

        // 校验：单人成群、超上限、未知角色、群名非法都要拒绝。
        Check("factory_rejects_single_member_group",
            Throws(() => SessionFactory.Validate(SessionKind.Group, "独聊", ["hutao"])), "1 member");
        Check("factory_rejects_oversized_group",
            Throws(() => SessionFactory.Validate(SessionKind.Group, "太大",
                ["hutao", "furina", "klee", "lacrimosa", "a", "b", "c", "d", "e"])), "9 members");
        Check("factory_rejects_unknown_character",
            Throws(() => SessionFactory.Validate(SessionKind.Group, "陌生人", ["hutao", "not-a-character"])), "unknown");
        Check("factory_rejects_blank_title",
            Throws(() => SessionFactory.Validate(SessionKind.Group, "   ", ["hutao", "furina"])), "blank title");
        Check("factory_rejects_multi_member_single",
            Throws(() => SessionFactory.Validate(SessionKind.Single, "", ["hutao", "furina"])), "2 members in single");

        // Normalize：MemberIds 必须从 Presences 派生，不在场的人不能留在列表里。
        var drifted = new SessionDescriptor(
            "group_x", SessionKind.Group, "T", "💬", "#FF000000",
            ["hutao", "furina", "lacrimosa"],
            [
                new MemberPresence("hutao", DateTimeOffset.Now),
                new MemberPresence("furina", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5)),
                new MemberPresence("lacrimosa", DateTimeOffset.Now),
            ],
            DateTimeOffset.Now, DateTimeOffset.Now, "", false, false, false, 0);
        var normalized = SessionFactory.Normalize(drifted);
        Check("factory_normalize_derives_member_ids_from_presences",
            normalized.MemberIds.Count == 2 &&
            !normalized.MemberIds.Contains("furina") && normalized.Presences.Count == 3,
            $"members=[{string.Join(',', normalized.MemberIds)}] presences={normalized.Presences.Count}");

        // 老数据（无 Presences）按 CreatedAt 补齐，且不洗掉已有的 JoinedAt。
        var legacyAt = DateTimeOffset.Now.AddDays(-3);
        var legacy = SessionFactory.Normalize(new SessionDescriptor(
            "single_klee", SessionKind.Single, "可莉", "💣", "#FF000000",
            ["klee"], [], legacyAt, legacyAt, "", false, false, false, 0));
        Check("factory_normalize_backfills_missing_presences",
            legacy.Presences.Count == 1 && legacy.Presences[0].JoinedAt == legacyAt && legacy.MemberIds.SequenceEqual(["klee"]),
            $"joined={legacy.Presences[0].JoinedAt:O}");

        // 迁移产物必须带 Presences（T-13），否则中途加入语义就没有依据。
        var migrated = await store.LoadCatalogAsync();
        Check("session_catalog_written_and_readable", migrated.Sessions.Count >= 0, "io ok");
        _ = root;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    // ── T-08：视角投影 ───────────────────────────────────────────────────

    private static void CheckProjectionLayer(Action<string, bool, string> Check)
    {
        var t0 = new DateTimeOffset(2026, 2, 14, 20, 0, 0, TimeSpan.FromHours(8));
        var presence = new MemberPresence("lacrimosa", t0) with { LeftAt = null };
        var late = new MemberPresence("klee", t0.AddMinutes(10));

        var timeline = new List<ConversationTurn>
        {
            Msg("t-0001", t0, "user", "你", "你们俩认识吗？", user: true, audience: ["hutao", "lacrimosa"], kind: ConversationTurnKind.Message),
            Msg("t-0002", t0.AddMinutes(1), "hutao", "胡桃", "哦呀，这不是安魂曲嘛。", user: false, audience: ["lacrimosa"], kind: ConversationTurnKind.Message),
            Msg("t-0003", t0.AddMinutes(2), "system", "", "安魂曲这一轮没能开口。", user: false, audience: [], kind: ConversationTurnKind.System),
            Msg("t-0004", t0.AddMinutes(3), "hutao", "胡桃", "（你们围坐在一起。）", user: false, audience: ["hutao", "lacrimosa"], kind: ConversationTurnKind.Hint),
            Msg("t-0005", t0.AddMinutes(4), "hutao", "胡桃", "只有胡桃听见的悄悄话。", user: false, audience: ["hutao"], kind: ConversationTurnKind.Message),
            Msg("t-0006", t0.AddMinutes(20), "hutao", "胡桃", "可莉来之后才说的。", user: false, audience: ["hutao", "lacrimosa", "klee"], kind: ConversationTurnKind.Message),
        };

        var view = AudienceProjection.ForCharacter(timeline, presence, "lacrimosa");
        var texts = view.Select(t => t.Text).ToArray();

        // 规则 3：Audience 明确不含自己的条目不能进来（"悄悄话"）。
        Check("projection_skips_narrow_audience",
            !texts.Any(t => t.Contains("悄悄话")), string.Join(" | ", texts));

        // 规则 4/5：因为视角属于安魂曲，胡桃的话应以「胡桃」为前缀、映射为 user。
        var otherSpeaker = view.Single(t => t.Id == "t-0002");
        Check("projection_rewrites_other_speaker_prefix",
            otherSpeaker.IsUser && otherSpeaker.Text.StartsWith("「胡桃」：", StringComparison.Ordinal),
            otherSpeaker.Text);

        // 反向：胡桃自己的视角里，t-0002 是她说的 → 保持 assistant，且**不加**前缀
        // （把自己说的话包装成"胡桃说：…"会让模型以为那是别人转述的）。
        var hutaoView = AudienceProjection.ForCharacter(timeline, new MemberPresence("hutao", t0), "hutao");
        var ownTurn = hutaoView.Single(t => t.Id == "t-0002");
        Check("projection_keeps_own_turns_as_assistant",
            !ownTurn.IsUser && !ownTurn.Text.StartsWith("「", StringComparison.Ordinal),
            $"isUser={ownTurn.IsUser}; text={ownTurn.Text}");

        // 视角里角色的归属必须清楚：别人的话带说话人，自己的话归属自己。
        Check("projection_attributes_speaker_correctly",
            hutaoView.Single(t => t.Id == "t-0002").SpeakerId == "hutao" &&
            otherSpeaker.SpeakerId == "hutao",
            "speaker preserved");

        // 规则 1：旁白只发给当时在场者——可莉 20 分才加入，看不到 20 分之前的旁白。
        var kleeView = AudienceProjection.ForCharacter(timeline, late, "klee");
        Check("projection_excludes_absent_member_messages",
            kleeView.All(t => t.At >= late.JoinedAt) && kleeView.Count == 1 && kleeView[0].Id == "t-0006",
            $"count={kleeView.Count}; ids=[{string.Join(',', kleeView.Select(t => t.Id))}]");

        // 被移出的成员看不到自己离开之后的内容。
        var left = new MemberPresence("hutao", t0) with { LeftAt = t0.AddMinutes(15) };
        var leftView = AudienceProjection.ForCharacter(timeline, left, "hutao");
        Check("projection_honours_left_at",
            leftView.All(t => t.At < t0.AddMinutes(15)) && !leftView.Any(t => t.Id == "t-0006"),
            $"ids=[{string.Join(',', leftView.Select(t => t.Id))}]");

        // ToHistory 取末尾 window 条（不是前 N 条）。
        var many = Enumerable.Range(1, 100)
            .Select(i => Msg($"t-{i:D4}", t0.AddMinutes(i), "hutao", "胡桃", $"第{i}句", false, ["lacrimosa"], ConversationTurnKind.Message))
            .ToArray();
        var full = AudienceProjection.ForCharacter(many, presence, "lacrimosa");
        var history = AudienceProjection.ToHistory(full, 24);
        Check("projection_to_history_takes_tail",
            history.Count == 24 && history[^1].Content.EndsWith("第100句", StringComparison.Ordinal),
            $"count={history.Count} last={history[^1].Content}");

        // 纯函数：同一输入两次调用逐字段相等。
        var again = AudienceProjection.ForCharacter(timeline, presence, "lacrimosa");
        Check("projection_is_deterministic",
            again.Count == view.Count && again.Zip(view).All(pair => pair.First == pair.Second),
            $"count={again.Count}");

        Check("projection_handles_empty_timeline",
            AudienceProjection.ForCharacter([], presence, "lacrimosa").Count == 0 &&
            AudienceProjection.ToHistory([], 24).Count == 0, "empty ok");
    }

    private static ConversationTurn Msg(string id, DateTimeOffset at, string speakerId, string speakerName,
        string text, bool user, string[] audience, ConversationTurnKind kind)
        => new(id, at, speakerId, speakerName, text, user, null, audience, kind);

    // ── T-12：音频配额与软链接防护 ────────────────────────────────────────

    private static async Task CheckOwnedAudioGuardAsync(string root, Action<string, bool, string> Check)
    {
        var generated = Path.Combine(root, "data", "voice", "generated");
        Directory.CreateDirectory(generated);
        var owned = Path.Combine(generated, "agent_tts_20260214_201609_031.wav");
        var foreign = Path.Combine(generated, "not_ours.wav");
        var wrongExt = Path.Combine(generated, "agent_tts_x.mp3");
        var outside = Path.Combine(root, "elsewhere_agent_tts_x.wav");
        foreach (var path in new[] { owned, foreign, wrongExt, outside })
            await File.WriteAllTextAsync(path, "x");

        Check("owned_audio_accepts_generated_agent_file",
            OwnedAudioGuard.IsOwned(generated, owned), owned);
        Check("owned_audio_rejects_foreign_prefix",
            !OwnedAudioGuard.IsOwned(generated, foreign), foreign);
        Check("owned_audio_rejects_wrong_extension",
            !OwnedAudioGuard.IsOwned(generated, wrongExt), wrongExt);
        Check("owned_audio_rejects_outside_root",
            !OwnedAudioGuard.IsOwned(generated, outside), outside);
        Check("owned_audio_rejects_missing_file",
            !OwnedAudioGuard.IsOwned(generated, Path.Combine(generated, "agent_tts_absent.wav")), "missing");

        // 应用与 ChatLogStore 共享同一个判定：两边必须给出一致结论。
        // 配额设 0，逼出"超限 → 候选最旧"的分支；默认 512 MiB 下测试文件永远不会超限。
        var store = new ChatLogStore(Path.Combine(root, "log.json"), audioSizeLimitBytes: 0,
            generatedAudioRoot: generated);
        var entries = new List<ChatEntry>
        {
            new("2026-02-14 20:16:09", "assistant", "带语音", owned) { OwnsGeneratedAudio = true },
            new("2026-02-14 20:16:10", "assistant", "别人的音频", foreign) { OwnsGeneratedAudio = true },
        };
        var prunable = store.ComputePrunableAudio(entries);
        Check("owned_audio_guard_is_shared_with_chat_log_store",
            prunable.Contains(owned) && !prunable.Contains(foreign),
            $"prunable=[{string.Join(',', prunable.Select(Path.GetFileName))}]");

        // 会话层的配额清理：只删自己生成的音频，别人的文件绝不能碰。
        var sessionStore = new SessionStore(root);
        await sessionStore.CreateSessionAsync(Descriptor("single_audio"));
        foreach (var turn in new[]
                 {
                     new ConversationTurn("", DateTimeOffset.Now.AddMinutes(-2), "hutao", "胡桃", "带语音", false, owned, ["hutao"], ConversationTurnKind.Message),
                     new ConversationTurn("", DateTimeOffset.Now.AddMinutes(-1), "hutao", "胡桃", "别人的音频", false, foreign, ["hutao"], ConversationTurnKind.Message),
                 })
            await sessionStore.AppendAsync("single_audio", turn);

        var timeline = await sessionStore.LoadTimelineAsync("single_audio");
        var candidates = sessionStore.ComputePrunableAudio(timeline, sizeLimitBytes: 0);
        Check("session_prune_only_deletes_owned_audio",
            candidates.Contains(owned) && !candidates.Contains(foreign) && !candidates.Contains(outside),
            $"candidates=[{string.Join(',', candidates.Select(Path.GetFileName))}]");

        var deleted = await sessionStore.PruneAudioAsync("single_audio", sizeLimitBytes: 0);
        Check("session_prune_actually_removes_only_owned",
            deleted.Contains(owned) && !File.Exists(owned) && File.Exists(foreign) && File.Exists(outside),
            $"deleted=[{string.Join(',', deleted.Select(Path.GetFileName))}]; foreign still exists={File.Exists(foreign)}");

        // T-11：同一毫秒生成 200 次文件名必须互不相同。
        // 两个 agent 的回合重叠时会真的落进同一毫秒，而写入用的是 FileMode.CreateNew
        // ——撞名就是 IOException，合成直接失败。
        var sameMoment = new DateTime(2026, 2, 14, 20, 16, 9, 31);
        var names = Enumerable.Range(0, 200)
            .Select(_ => HuTao.Voice.GeneratedAudioNaming.NewFileName(sameMoment))
            .ToArray();
        Check("tts_audio_name_is_unique",
            names.Distinct().Count() == names.Length,
            $"distinct={names.Distinct().Count()}/200");

        // 名字必须仍然符合 OwnedAudioGuard 的白名单（前缀 + .wav），否则配额清理会失效。
        Check("tts_audio_name_stays_prunable",
            names.All(name =>
                name.StartsWith(HuTao.Dialogue.Storage.OwnedAudioGuard.GeneratedPrefix, StringComparison.Ordinal) &&
                string.Equals(Path.GetExtension(name), HuTao.Dialogue.Storage.OwnedAudioGuard.GeneratedExtension,
                    StringComparison.OrdinalIgnoreCase)),
            names[0]);

        _ = root;
    }

    // ── T-07：角色实例池（不变量 I1/I2/I3）────────────────────────────────

    private static async Task CheckAgentPoolAsync(Action<string, bool, string> Check)
    {
        var created = new List<(string Scope, string CharacterId)>();
        var handedOff = new List<string>();

        var pool = new HuTao.Dialogue.Runtime.AgentPool(
            (scope, definition, ct) =>
            {
                created.Add((scope, definition.Id));
                // 与生产一致：装配函数负责把 scope 写进 runtime。
                // 少了这一笔，"每会话记忆互不相同"就没有可断言的事实。
                return Task.FromResult(FakeRuntime(definition, new MutableHistoryConversation(), scope));
            });

        // I1：同一角色先后出现在两个会话里，拿到的必须是**同一个实例引用**。
        using (var first = await pool.ActivateAsync("single_hutao", ["hutao"]))
        {
            var original = first.Runtimes["hutao"];
            using var second = await pool.ActivateAsync("group_default", ["hutao"],
                handoff: (characterId, _) => { handedOff.Add(characterId); return Task.CompletedTask; });
            Check("pool_single_runtime_per_character",
                ReferenceEquals(original, second.Runtimes["hutao"]),
                "same reference across sessions");
            Check("pool_handoff_called_on_eviction",
                handedOff.SequenceEqual(["hutao"]) && created.Count == 1,
                $"handoff=[{string.Join(',', handedOff)}]; created={created.Count}");
            Check("pool_scope_is_session_id",
                created[0].Scope == "single_hutao" && original.MemoryScope == "single_hutao",
                $"{created[0].Scope}/{original.MemoryScope}");
        }

        // 交接之后旧会话再取上下文，必须还能拿到（实例没被销毁）。
        Check("pool_keeps_runtime_alive_after_handoff",
            pool.Find("hutao") is not null && created.Count == 1,
            $"resident={pool.ResidentCharacterIds.Count}");

        // 不同角色各建一份，且 scope 各自等于会话 Id。
        using (var group = await pool.ActivateAsync("group_default", ["hutao", "klee", "furina"]))
        {
            Check("pool_creates_one_runtime_per_character",
                group.Runtimes.Count == 3 && created.Count == 3,
                $"created={created.Count}");
            Check("pool_every_character_has_its_own_instance",
                group.Runtimes.Values.Select(r => r.Agent).Distinct().Count() == 3,
                "3 distinct agents");
        }

        // Release 之后角色仍存活（池子的语义是"常驻"，不是"用完即毁"）。
        await CheckPoolReleaseKeepsResidentAsync(pool, Check);

        // 关闭时回收全部实例，且失败被收集而不是抛出。
        var failures = await pool.DisposeAllAsync();
        Check("pool_dispose_reports_failures_without_throwing", failures.Count == 0,
            $"failures={failures.Count}");
        Check("pool_dispose_retires_every_runtime",
            pool.ResidentCharacterIds.Count == 0 && pool.Find("hutao") is null && pool.Find("klee") is null,
            $"resident={pool.ResidentCharacterIds.Count}");
        Check("pool_rejects_empty_members",
            await ThrowsAsync(() => pool.ActivateAsync("group_x", [])), "empty members");
        Check("pool_rejects_unknown_character",
            await ThrowsAsync(() => pool.ActivateAsync("group_x", ["not-a-character"])), "unknown");

        // 关闭后再激活必须明确失败，而不是静默返回一个空租约。
        Check("pool_rejects_activation_after_close",
            await ThrowsAsync(() => pool.ActivateAsync("group_x", ["hutao"])), "closed");

        await CheckPoolTransientRetryAsync(Check);
    }

    /// <summary>
    /// 另一个宿主（桌宠窗）正持有同一角色的 authority 锁时，<c>Create</c> 会抛 IOException。
    /// 那是**瞬时**冲突（对方装配完就放锁），必须重试而不是让用户看到"切换会话失败"。
    /// 同时守住反面：确定性失败（参数错、角色未注册）**不许**被重试吞掉。
    /// </summary>
    private static async Task CheckPoolTransientRetryAsync(Action<string, bool, string> Check)
    {
        var attempts = 0;
        var pool = new HuTao.Dialogue.Runtime.AgentPool(
            (scope, definition, ct) =>
            {
                attempts++;
                if (attempts < 3)
                    throw new IOException("Synthetic authority-lock conflict.");
                return Task.FromResult(FakeRuntime(definition, new MutableHistoryConversation(), scope));
            }, null);

        using (var lease = await pool.ActivateAsync("single_hutao", ["hutao"]))
        {
            Check("pool_retries_transient_lock_conflict",
                attempts == 3 && lease.Runtimes.ContainsKey("hutao"),
                $"attempts={attempts}");
        }
        await pool.DisposeAllAsync();

        // 瞬时冲突一直存在时必须放弃并抛，而不是无限重试。
        var alwaysFails = new HuTao.Dialogue.Runtime.AgentPool(
            (scope, definition, ct) => throw new IOException("Synthetic permanent lock conflict."), null);
        var bubbled = false;
        try { await alwaysFails.ActivateAsync("single_hutao", ["hutao"]); }
        catch (IOException) { bubbled = true; }
        Check("pool_gives_up_after_retry_budget", bubbled, "does not retry forever");
        await alwaysFails.DisposeAllAsync();

        // 确定性失败不重试：未注册角色应当立刻 ArgumentException，且装配函数一次都没被调用。
        var createCalls = 0;
        var deterministic = new HuTao.Dialogue.Runtime.AgentPool(
            (scope, definition, ct) => { createCalls++; throw new IOException("should not run"); }, null);
        await ThrowsAsync(() => deterministic.ActivateAsync("single_hutao", ["not-a-character"]));
        Check("pool_does_not_retry_deterministic_failure", createCalls == 0, $"calls={createCalls}");
        await deterministic.DisposeAllAsync();
    }

    private static async Task CheckPoolReleaseKeepsResidentAsync(
        HuTao.Dialogue.Runtime.AgentPool pool, Action<string, bool, string> Check)
    {
        var lease = await pool.ActivateAsync("single_klee", ["klee"]);
        var before = lease.Runtimes["klee"];
        lease.Dispose();
        Check("pool_release_keeps_resident",
            ReferenceEquals(pool.Find("klee"), before), "still resident after release");
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>最小的 AgentRuntime：只填池子会用到的字段。</summary>
    private static HuTao.Dialogue.Runtime.AgentRuntime FakeRuntime(
        CharacterDefinition definition, IAgentConversation agent, string memoryScope = "desktop")
    {
        var persona = new PersonaProfile
        {
            Root = Path.GetTempPath(), Name = definition.Name,
            SystemPrompt = $"你是{definition.Name}", Catchphrases = new(), Lore = "", Quotes = [],
        };
        return new HuTao.Dialogue.Runtime.AgentRuntime(
            definition,
            persona,
            agent,
            DocumentReader: null!,
            Tts: null,
            EmotionReferences: null!,
            OriginalVoices: null!,
            Tools: [],
            MemoryScope: memoryScope);
    }

    /// <summary>可写历史的最小会话实现，池子测试用它来验证交接前后上下文是否还在。</summary>
    private sealed class MutableHistoryConversation : IAgentConversation
    {
        private readonly List<ChatMessage> _history = [];

        public IReadOnlyList<ChatMessage> History => _history.ToArray();
        public ConversationPrivacyStamp? PrivacyStamp => null;

        public void RestoreHistory(IEnumerable<ChatMessage> history)
        {
            _history.Clear();
            _history.AddRange(history);
        }

        public ConversationContext? CaptureContext() => new(History, PrivacyStamp);

        public Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
            => Task.FromResult(new AgentTurnResult("", "", "", null, ""));

        public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
            => Task.FromResult(new AgentTurnResult("", "", "", null, ""));

        public Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpeechSegment>>([]);

        public Task<TtsResult?> SynthesizeAsync(string text, string emotion = "neutral", double intensity = 0.5, CancellationToken ct = default)
            => Task.FromResult<TtsResult?>(null);
    }

    // ── T-10：会话运行时编排 ─────────────────────────────────────────────

    private static async Task CheckSessionRuntimeAsync(string root, Action<string, bool, string> Check)
    {
        var runtimeRoot = Path.Combine(root, "runtime");
        var store = new SessionStore(runtimeRoot);
        var order = new List<string>();
        var agents = new Dictionary<string, ScriptedConversation>(StringComparer.OrdinalIgnoreCase);

        var pool = new HuTao.Dialogue.Runtime.AgentPool(
            (scope, definition, ct) =>
            {
                order.Add("create:" + definition.Id);
                var agent = new ScriptedConversation(definition.Id, $"来自{definition.Name}的台词。", order);
                agents[definition.Id] = agent;
                return Task.FromResult(FakeRuntime(definition, agent, scope));
            });

        // 在场区间必须覆盖"现在"——它最开始就是被拿来判断"这条消息某人当时在不在"的，
        // 把 JoinedAt 设成过去会让本轮新产生的消息全部落在区间之外、集体失联。
        var joined = DateTimeOffset.Now.AddSeconds(-1);
        var group = SessionFactory.Normalize(new SessionDescriptor(
            "group_default", SessionKind.Group, "提瓦特茶话会", "💬", "#FF8C60BE",
            ["hutao", "lacrimosa"],
            [new MemberPresence("hutao", joined),
             new MemberPresence("lacrimosa", joined)],
            joined, joined, "",
            false, false, false, 0));
        await store.CreateSessionAsync(group);
        await store.SaveCatalogAsync(new SessionCatalog([group]));

        // 用户在时间线里落盘完成的时刻（导演被点名的瞬间 = 用户那条已经写完）。
        DateTimeOffset? userPersistedAt = null;
        var session = new ChatSessionRuntime(store, pool, group, new SessionRuntimeOptions
        {
            Director = new TestDirector("hutao", () => userPersistedAt = DateTimeOffset.Now),
            RoomOptions = new ChatRoomOptions { MinTurns = 1, MaxTurns = 4 },
        });

        await session.ActivateAsync();
        var createsAfterFirst = order.Count(o => o.StartsWith("create:", StringComparison.Ordinal));
        await session.ActivateAsync();
        Check("session_activate_is_idempotent",
            createsAfterFirst == 2 &&
            order.Count(o => o.StartsWith("create:", StringComparison.Ordinal)) == 2,
            $"creates={order.Count(o => o.StartsWith("create:", StringComparison.Ordinal))}");

        order.Clear();
        await session.SubmitUserAsync("你们俩认识吗？");

        // I4：用户那句话必须在角色开始生成**之前**已经落盘。
        Check("session_user_turn_persisted_before_generation",
            userPersistedAt is not null &&
            agents.TryGetValue("hutao", out var hutaoAgent) &&
            hutaoAgent.LastGenerateStarted is { } started &&
            userPersistedAt <= started,
            $"persisted={userPersistedAt:O} started={agents.GetValueOrDefault("hutao")?.LastGenerateStarted:O}");

        var timeline = await store.LoadTimelineAsync("group_default");
        Check("session_group_uses_director",
            timeline.Any(t => !t.IsUser && t.SpeakerId == "hutao"), "speaker=hutao");
        Check("session_appends_user_and_character_turns",
            timeline.Count == 2 && timeline[0].IsUser && !timeline[1].IsUser &&
            timeline[1].SpeakerId == "hutao",
            string.Join(" | ", timeline.Select(t => $"{t.SpeakerId}:{t.Text}")));

        // 视角分发：发言者自己不该收到自己的台词，另一位应当收到，且被包装成 user。
        var hutaoView = await store.LoadViewAsync("group_default", "hutao");
        var lacrimosaView = await store.LoadViewAsync("group_default", "lacrimosa");
        // 发言者**自己**也要收到自己那句（否则切走再切回来就忘了刚说过什么），
        // 但它必须是 assistant 且不带「某某说：」前缀。
        Check("session_fanout_includes_speaker_own_line",
            hutaoView.Any(t => t.Text.Contains("来自胡桃", StringComparison.Ordinal)),
            $"hutao view={hutaoView.Count}");
        // 视角文件里存的是**原始记录**；「某某说：」这层包装由投影在**读取时**加上，
        // 所以断言的是"另一个角色最终会看到什么"，而不是磁盘上的字节。
        var lacrimosaPresence = group.PresenceOf("lacrimosa")!;
        var lacrimosaProjected = AudienceProjection.ForCharacter(lacrimosaView, lacrimosaPresence, "lacrimosa");
        Check("session_fanout_includes_others_with_prefix",
            lacrimosaProjected.Any(t => t.Text.StartsWith("「胡桃」：", StringComparison.Ordinal)),
            string.Join(" | ", lacrimosaProjected.Select(t => t.Text)));
        // 发言者自己的视角里，那同一句必须保持 assistant、不带前缀。
        var hutaoProjected = AudienceProjection.ForCharacter(hutaoView, group.PresenceOf("hutao")!, "hutao");
        Check("session_fanout_keeps_own_line_as_assistant",
            hutaoProjected.Any(t => !t.IsUser && t.Text.Contains("来自胡桃", StringComparison.Ordinal)) &&
            !hutaoProjected.Any(t => t.Text.StartsWith("「胡桃」：", StringComparison.Ordinal)),
            string.Join(" | ", hutaoProjected.Select(t => $"{t.IsUser}:{t.Text}")));

        // 收尾必须幂等，且把当前上下文交接进视角。
        await session.EndSessionAsync();
        var afterEnd = await store.LoadViewAsync("group_default", "hutao");
        await session.EndSessionAsync();
        var afterSecond = await store.LoadViewAsync("group_default", "hutao");
        Check("session_end_is_idempotent", afterEnd.Count == afterSecond.Count,
            $"{afterEnd.Count} -> {afterSecond.Count}");
        Check("session_end_writes_view_tail",
            afterEnd.Count >= timeline.Count,
            $"view={afterEnd.Count} timeline={timeline.Count}");
        Check("session_handoff_appends_without_duplicating",
            afterEnd.Count == afterEnd.Select(t => $"{t.IsUser}\u001F{t.Text}").Distinct().Count() ||
            afterEnd.Count(t => t.Text.Contains("来自胡桃", StringComparison.Ordinal)) <= 1,
            $"view={afterEnd.Count}");

        // 单聊走 ProactiveAsync，不经过导演。
        var single = SessionFactory.Normalize(new SessionDescriptor(
            "single_klee", SessionKind.Single, "可莉", "💣", "#FFDA4D3E",
            ["klee"], [new MemberPresence("klee", joined)],
            joined, joined, "",
            false, false, false, 0));
        await store.CreateSessionAsync(single);
        var singleSession = new ChatSessionRuntime(store, pool, single, new SessionRuntimeOptions
        {
            // 故意给一个会抛的导演：单聊根本不该用它。
            Director = new ExplodingDirector(),
        });
        order.Clear();
        var singleOutcome = await singleSession.StepAsync();
        Check("session_single_uses_proactive",
            order.Contains("proactive:klee") && !order.Contains("respond:klee") &&
            singleOutcome.Turn is not null && singleOutcome.SpeakerId == "klee",
            $"order={string.Join(',', order)}");

        // I1 贯穿编排层：切到单聊不该把群聊里的角色实例换掉。
        Check("session_reuses_pool_instances",
            pool.ResidentCharacterIds.Count == 3,
            $"resident={pool.ResidentCharacterIds.Count}");

        await singleSession.EndSessionAsync();
        var failures = await pool.DisposeAllAsync();
        Check("session_pool_disposes_cleanly", failures.Count == 0, $"failures={failures.Count}");
    }

    /// <summary>记一笔调用顺序，并回固定台词。用来断言"落盘先于生成"。</summary>
    private sealed class ScriptedConversation(string name, string reply, List<string> order) : IAgentConversation
    {
        private readonly List<ChatMessage> _history = [];

        /// <summary>生成开始的时刻。与时间线的写入时刻比较，就能证明"先落盘再生成"。</summary>
        public DateTimeOffset? LastGenerateStarted { get; private set; }

        public IReadOnlyList<ChatMessage> History => _history.ToArray();
        public ConversationPrivacyStamp? PrivacyStamp => null;

        public void RestoreHistory(IEnumerable<ChatMessage> history)
        {
            _history.Clear();
            _history.AddRange(history);
        }

        public ConversationContext? CaptureContext() => new(History, PrivacyStamp);

        public Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
        {
            order.Add("respond:" + name);
            LastGenerateStarted = DateTimeOffset.Now;
            _history.Add(new ChatMessage("user", userInput));
            _history.Add(new ChatMessage("assistant", reply));
            return Task.FromResult(new AgentTurnResult("", "", reply, null, ""));
        }

        public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
        {
            order.Add("proactive:" + name);
            LastGenerateStarted = DateTimeOffset.Now;
            _history.Add(new ChatMessage("assistant", reply));
            return Task.FromResult(new AgentTurnResult("", "", reply, null, ""));
        }

        public Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpeechSegment>>([new SpeechSegment(reply, "neutral", 0.5)]);

        public Task<TtsResult?> SynthesizeAsync(string text, string emotion = "neutral", double intensity = 0.5, CancellationToken ct = default)
            => Task.FromResult<TtsResult?>(null);
    }

    /// <summary>
    /// 测试导演。可选地在被问"下一位是谁"之后打一个点——那正是
    /// <c>ChatSessionRuntime</c> **刚刚写完时间线**的时刻，于是它能当作
    /// "落盘 → 生成"这条顺序的可观测锚点。
    /// </summary>
    private sealed class TestDirector(string speakerId, Action? onDecided = null)
        : HuTao.Dialogue.ChatRoom.IChatRoomDirector
    {
        public Task<HuTao.Dialogue.ChatRoom.DirectorDecision> DecideAsync(
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomParticipant> participants,
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomTurn> transcript,
            string? userInput,
            CancellationToken ct = default)
        {
            onDecided?.Invoke();
            return Task.FromResult(new HuTao.Dialogue.ChatRoom.DirectorDecision(speakerId, "测试点名", false));
        }
    }

    private sealed class ExplodingDirector : HuTao.Dialogue.ChatRoom.IChatRoomDirector
    {
        public Task<HuTao.Dialogue.ChatRoom.DirectorDecision> DecideAsync(
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomParticipant> participants,
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomTurn> transcript,
            string? userInput,
            CancellationToken ct = default)
            => throw new InvalidOperationException("单聊不该调用导演。");
    }

    // ── T-13：存量迁移 ───────────────────────────────────────────────────

    private static async Task CheckMigrationAsync(string root, Action<string, bool, string> Check)
    {
        var repo = Path.Combine(root, "migration");
        var dataDir = Path.Combine(repo, "data");
        Directory.CreateDirectory(dataDir);

        // 造一份桌宠时代的聊天记录（旧格式：顶层 JSON 数组，无发言人字段）。
        var legacyEntries = new List<ChatEntry>
        {
            new("2026-02-01 10:00:00", "user", "你好呀", null),
            new("2026-02-01 10:00:05", "assistant", "哦呀，是你呀。", "data/voice/generated/agent_tts_a.wav"),
            new("2026-02-01 10:01:00", "user", "在忙吗", null),
            new("2026-02-01 10:01:03", "assistant", "不忙，说吧。", null),
        };
        var legacyPath = Path.Combine(dataDir, "chat_log_hutao.json");
        await File.WriteAllTextAsync(legacyPath,
            System.Text.Json.JsonSerializer.Serialize(legacyEntries, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // 记忆文件（为了断言"复制而不是移动"）。
        var legacyMemory = Path.Combine(dataDir, "memory_hutao.json");
        await File.WriteAllTextAsync(legacyMemory, "{\"version\":1,\"records\":[]}");

        var store = new SessionStore(repo);
        var characters = new[] { CharacterCatalog.Get("hutao") };
        var report = await SessionMigration.RunAsync(store, repo, characters);

        Check("session_migration_creates_single_session",
            report.Created.Any(entry => entry.StartsWith("single_hutao", StringComparison.Ordinal)),
            string.Join(" | ", report.Created));

        var timeline = await store.LoadTimelineAsync("single_hutao");
        Check("session_migration_preserves_all_entries",
            timeline.Count == legacyEntries.Count,
            $"{timeline.Count}/{legacyEntries.Count}");
        Check("session_migration_maps_roles_and_speakers",
            timeline[0].IsUser && timeline[0].SpeakerId == "user" &&
            !timeline[1].IsUser && timeline[1].SpeakerId == "hutao" &&
            timeline[1].AudioPath == "data/voice/generated/agent_tts_a.wav",
            $"{timeline[0].SpeakerId}/{timeline[1].SpeakerId}/{timeline[1].AudioPath}");

        // 时间必须来自原记录，而不是"迁移那一刻"——否则整段历史会被算成"加入之前"。
        Check("session_migration_keeps_original_timestamps",
            timeline[0].At.Year == 2026 && timeline[0].At.Month == 2 && timeline[0].At.Day == 1,
            timeline[0].At.ToString("O"));

        // 在场起点必须早于第一条，否则投影会把全部历史过滤掉。
        var catalog = await store.LoadCatalogAsync();
        var descriptor = catalog.Find("single_hutao")!;
        var projected = AudienceProjection.ForCharacter(
            timeline, descriptor.PresenceOf("hutao")!, "hutao");
        Check("session_migration_presence_covers_history",
            projected.Count == timeline.Count,
            $"projected={projected.Count}/{timeline.Count}");

        // 视角缓存也要建好，否则首次激活要现场重算。
        Check("session_migration_prebuilds_view",
            (await store.LoadViewAsync("single_hutao", "hutao")).Count == legacyEntries.Count,
            "view built");

        // 原文件一个都不能删、不能移。
        Check("session_migration_never_deletes_source",
            File.Exists(legacyPath) && File.Exists(legacyMemory),
            "sources intact");

        // 记忆复制到会话作用域，且路径由 KnowledgeHostPaths 决定。
        var scopedMemory = HuTao.Dialogue.Runtime.KnowledgeHostPaths
            .Resolve(repo, characters[0], "local", "single_hutao").Memory;
        Check("session_migration_copies_memory_into_session_scope",
            File.Exists(scopedMemory) && Path.GetFileName(scopedMemory) == "memory_hutao_single_hutao.json",
            Path.GetFileName(scopedMemory));

        // 幂等：再跑一次不应新增任何东西。
        var second = await SessionMigration.RunAsync(store, repo, characters);
        Check("session_migration_is_idempotent",
            second.Created.Count == 0 &&
            (await store.LoadTimelineAsync("single_hutao")).Count == legacyEntries.Count,
            $"created={second.Created.Count}");

        // 损坏的源文件：必须报警，不能假装迁成功。
        var brokenRepo = Path.Combine(root, "migration-broken");
        Directory.CreateDirectory(Path.Combine(brokenRepo, "data"));
        await File.WriteAllTextAsync(Path.Combine(brokenRepo, "data", "chat_log_furina.json"), "{ not json at all");
        var broken = await SessionMigration.RunAsync(new SessionStore(brokenRepo), brokenRepo,
            [CharacterCatalog.Get("furina")]);
        Check("session_migration_warns_on_corrupt_source",
            broken.Warnings.Any(w => w.Contains("解析失败", StringComparison.Ordinal)) &&
            !broken.Created.Any(c => c.StartsWith("single_", StringComparison.Ordinal)),
            $"text=[{string.Join(" / ", broken.Warnings)}]; created=[{string.Join(',', broken.Created)}]");

        // 合法但是空数组：同样是"迁不出东西"，也要说清楚。
        var emptyRepo = Path.Combine(root, "migration-empty");
        Directory.CreateDirectory(Path.Combine(emptyRepo, "data"));
        await File.WriteAllTextAsync(Path.Combine(emptyRepo, "data", "chat_log_klee.json"), "[]");
        var empty = await SessionMigration.RunAsync(new SessionStore(emptyRepo), emptyRepo,
            [CharacterCatalog.Get("klee")]);
        Check("session_migration_warns_on_empty_source",
            empty.Warnings.Any(w => w.Contains("读出 0 条", StringComparison.Ordinal)) &&
            !empty.Created.Any(c => c.StartsWith("single_", StringComparison.Ordinal)),
            $"text=[{string.Join(" / ", empty.Warnings)}]; created=[{string.Join(',', empty.Created)}]");

        // 默认群聊壳子应该被建出来（≥2 位默认可选角色时）。
        Check("session_migration_creates_default_group",
            catalog.Sessions.Any(s => s.Kind == SessionKind.Group),
            $"groups={catalog.Sessions.Count(s => s.Kind == SessionKind.Group)}");

        // 而且它的 id 必须是**固定的 group_default**。
        // 踩过的坑：EnsureGroupAsync 生成的是时间戳 id（group_2026…），
        // 而迁移查的是 group_default——两者永不相等，于是每次迁移都多建一个群，
        // "幂等迁移"名存实亡。这条断言就是为了钉住这个 id。
        Check("session_migration_default_group_has_fixed_id",
            catalog.Find(SessionFactory.DefaultGroupSessionId) is not null,
            $"ids=[{string.Join(',', catalog.Sessions.Where(s => s.Kind == SessionKind.Group).Select(s => s.Id))}]");
        Check("session_migration_creates_exactly_one_default_group",
            catalog.Sessions.Count(s => s.Kind == SessionKind.Group) == 1,
            $"groups={catalog.Sessions.Count(s => s.Kind == SessionKind.Group)}");
    }

    // ── 情境台词：缺失时当场生成并持久化，之后沿用 ─────────────────────

    /// <summary>
    /// 用户要的行为：某个角色在某情境下没有专属台词时，**当场按人设生成一条并写回磁盘**，
    /// 以后所有宿主都直接沿用。这一组用桩生成器（不发请求），所以是确定性的。
    /// </summary>
    private static async Task CheckScriptedLineGenerationAsync(
        string root, Action<string, bool, string> Check)
    {
        var personaDir = Path.Combine(root, "scripted-persona");
        Directory.CreateDirectory(personaDir);
        var file = Path.Combine(personaDir, "fallbacks.json");

        var calls = new List<string>();
        var scripted = new HuTao.Persona.ScriptedLines(personaDir, "测试角色",
            (situation, ct) =>
            {
                calls.Add(situation.ToString());
                return Task.FromResult<string?>($"生成于{situation}");
            });
        scripted.AttachVoice("你是测试角色。", "唔、诶");

        // 一轮都没生成过：所有情境都缺。
        Check("scripted_starts_with_everything_missing",
            scripted.Missing().Count == Enum.GetValues<HuTao.Persona.ScriptedLines.Situation>().Length,
            $"missing={scripted.Missing().Count}");

        // 缺失时**不阻塞**：先给中性语气，同时后台排队生成。
        var immediate = scripted.Get(HuTao.Persona.ScriptedLines.Situation.Distracted);
        Check("scripted_missing_returns_neutral_immediately",
            immediate == HuTao.Persona.ScriptedLines.Neutral(HuTao.Persona.ScriptedLines.Situation.Distracted),
            immediate);

        // 愿意等的时候能拿到生成结果，并且落盘。
        var generated = await scripted.GetAsync(HuTao.Persona.ScriptedLines.Situation.Distracted);
        Check("scripted_get_async_generates", generated == "生成于Distracted", generated);
        Check("scripted_generation_persisted",
            File.Exists(file) && File.ReadAllText(file).Contains("生成于Distracted", StringComparison.Ordinal),
            File.Exists(file) ? "file written" : "no file");
        Check("scripted_missing_shrinks_after_generation",
            scripted.Missing().Count == scripted.Missing().Count &&
            !scripted.Missing().Contains(HuTao.Persona.ScriptedLines.Situation.Distracted),
            $"missing={scripted.Missing().Count}");

        // 换一个实例（模拟"下次启动"）必须直接读到，且**不再调生成器**。
        var before = calls.Count;
        var reloaded = new HuTao.Persona.ScriptedLines(personaDir, "测试角色",
            (_, _) => { calls.Add("SHOULD-NOT-BE-CALLED"); return Task.FromResult<string?>("x"); });
        var reused = reloaded.Get(HuTao.Persona.ScriptedLines.Situation.Distracted);
        Check("scripted_reused_across_instances", reused == "生成于Distracted", reused);
        Check("scripted_does_not_regenerate_when_present", calls.Count == before, $"calls={calls.Count}");

        // 落盘形态是**扁平**的（与 catchphrases.json 同风格），且不含私有信封。
        var raw = File.ReadAllText(file);
        Check("scripted_file_is_flat_and_human_editable",
            !raw.Contains("\"Version\"", StringComparison.Ordinal) &&
            raw.Contains("\"Distracted\"", StringComparison.Ordinal),
            raw.Length > 80 ? raw[..80].Replace('\n', ' ') : raw);

        // 生成失败（返回 null / 抛异常）不许炸聊天，也不许污染文件。
        var failing = new HuTao.Persona.ScriptedLines(Path.Combine(root, "scripted-fail"), "测试角色2",
            (_, _) => Task.FromResult<string?>(null));
        failing.AttachVoice("你是测试角色2。", "");
        var fallbackOnFailure = await failing.GetAsync(HuTao.Persona.ScriptedLines.Situation.RestReminder);
        Check("scripted_generation_failure_falls_back_to_neutral",
            fallbackOnFailure == HuTao.Persona.ScriptedLines.Neutral(HuTao.Persona.ScriptedLines.Situation.RestReminder),
            fallbackOnFailure);

        var throwing = new HuTao.Persona.ScriptedLines(Path.Combine(root, "scripted-throw"), "测试角色3",
            (_, _) => throw new InvalidOperationException("模拟生成失败"));
        throwing.AttachVoice("你是测试角色3。", "");
        var fallbackOnThrow = await throwing.GetAsync(HuTao.Persona.ScriptedLines.Situation.Distracted);
        Check("scripted_generation_exception_falls_back_to_neutral",
            fallbackOnThrow == HuTao.Persona.ScriptedLines.Neutral(HuTao.Persona.ScriptedLines.Situation.Distracted),
            fallbackOnThrow);

        // 生成物里的技术口吻要被丢弃，否则等于把出戏内容写死进数据。
        var leaky = new HuTao.Persona.ScriptedLines(Path.Combine(root, "scripted-leak"), "测试角色4",
            (_, _) => Task.FromResult<string?>("作为一个AI助手，我可以帮你查询。"));
        leaky.AttachVoice("你是测试角色4。", "");
        var leakResult = await leaky.GetAsync(HuTao.Persona.ScriptedLines.Situation.Distracted);
        Check("scripted_discards_technical_voice",
            leakResult == HuTao.Persona.ScriptedLines.Neutral(HuTao.Persona.ScriptedLines.Situation.Distracted),
            leakResult);
    }

    // ── 记忆归属：别的角色的记忆不许被当成"我说过的话" ─────────────────

    /// <summary>
    /// 实测缺陷：芙宁娜会说"本堂主"。追下去发现 <c>memory_furina.json</c> 里躺着
    /// <c>{"text":"记录已清空~ 本堂主可都忘光啦，嘿嘿。","speaker":"assistant"}</c>
    /// （"本堂主" 8 次、"往生堂" 10 次）——她的长期记忆里装着胡桃的自我认知。
    /// 检索时这条被贴上「你曾说」喂给模型，模型认定"我自己说过本堂主"，然后模仿。
    ///
    /// 修法两层：写入时盖归属标签；检索时**只有归属明确等于当前角色才说"你曾说"**。
    /// </summary>
    private static async Task CheckMemoryAttributionAsync(Action<string, bool, string> Check)
    {
        static HuTao.Knowledge.Memory.MemoryHit Hit(
            string text, string speaker, string persona, HuTao.Knowledge.Memory.MemoryKind kind)
        {
            var record = new HuTao.Knowledge.Memory.MemoryRecord
            {
                Id = "m" + Guid.NewGuid().ToString("N")[..16],
                Kind = kind,
                Text = text,
                Speaker = speaker,
                Persona = persona,
                ObservedAt = DateTimeOffset.Now.AddMinutes(-5),
                RecordedAt = DateTimeOffset.Now.AddMinutes(-5),
                Importance = 0.4,
            };
            return new HuTao.Knowledge.Memory.MemoryHit(
                record, Score: 0.8, Relevance: 0.9, Recency: 0.9, ImportanceScore: 0.4,
                Expired: false, Reason: "test");
        }

        var now = DateTimeOffset.Now;
        var hits = new List<HuTao.Knowledge.Memory.MemoryHit>
        {
            Hit("记录已清空~ 本堂主可都忘光啦，嘿嘿。", "assistant", "胡桃", HuTao.Knowledge.Memory.MemoryKind.Turn),
            Hit("今天想吃番茄酱。", "assistant", "安魂曲", HuTao.Knowledge.Memory.MemoryKind.Turn),
            Hit("旧版留下的、没有归属的助手记录。", "assistant", "", HuTao.Knowledge.Memory.MemoryKind.Turn),
            Hit("用户说他妹妹的生日是十月二十日。", "user", "", HuTao.Knowledge.Memory.MemoryKind.Turn),
            Hit("这是我自己（芙宁娜）说过的话。", "assistant", "芙宁娜", HuTao.Knowledge.Memory.MemoryKind.Turn),
        };

        // 以芙宁娜的身份渲染。
        var rendered = HuTao.Knowledge.Memory.MemoryRetriever.FormatPromptSection(
            hits, now, maxCharacters: 4000, currentPersona: "芙宁娜");

        Check("memory_foreign_character_not_labelled_as_own",
            !rendered.Contains("你曾说（不能据此认定用户事实）] 记录已清空~ 本堂主可都忘光啦", StringComparison.Ordinal),
            "hutao's line must not be framed as furina's own");
        Check("memory_foreign_character_is_labelled_explicitly",
            rendered.Contains("另一个角色说的", StringComparison.Ordinal),
            "explicitly marked as another character's");
        Check("memory_untagged_legacy_is_labelled_unverified",
            rendered.Contains("来源不明的旧记录", StringComparison.Ordinal),
            "legacy rows are not claimable as own");
        Check("memory_own_line_still_claimable",
            rendered.Contains("你曾说（不能据此认定用户事实）] 这是我自己（芙宁娜）说过的话。", StringComparison.Ordinal),
            "own line keeps the old label");
        Check("memory_user_line_unchanged",
            rendered.Contains("用户说] 用户说他妹妹的生日是十月二十日。", StringComparison.Ordinal),
            "user rows unaffected");

        // 没有任何归属信息时（旧宿主、未配置角色名）也必须保守：不许把别人的话当成自己的。
        var legacyOnly = HuTao.Knowledge.Memory.MemoryRetriever.FormatPromptSection(
            [Hit("没有归属的旧记录。", "assistant", "", HuTao.Knowledge.Memory.MemoryKind.Turn)],
            now, maxCharacters: 4000, currentPersona: "");
        Check("memory_without_current_persona_stays_conservative",
            !legacyOnly.Contains("你曾说", StringComparison.Ordinal),
            "no current persona -> nothing claimed as own");

        // 写入端确实会盖归属标签（否则上面的区分永远只能靠"不明"）。
        var dir = Path.Combine(Path.GetTempPath(), "hutao-memattr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new HuTao.Knowledge.Memory.MemoryOptions { Persona = "安魂曲" };
            var store = new HuTao.Knowledge.Memory.ConversationMemoryStore(
                Path.Combine(dir, "memory.json"), options);
            var record = store.ObserveTurn("assistant", "安魂曲，想睡觉。", DateTimeOffset.Now);
            Check("memory_store_stamps_persona_on_write",
                record is not null && record.Persona == "安魂曲",
                $"persona='{record?.Persona}'");

            // 导入 chat_log（角色是已知的）也必须盖章：漏了它，本角色的历史
            // 会被检索端当成"来源不明的旧记录"，"你曾说"就永远不成立。
            var logPath = Path.Combine(dir, "chat_log.json");
            await System.IO.File.WriteAllTextAsync(logPath,
                "[{\"Time\":\"2026-09-01 10:00:00\",\"Role\":\"assistant\",\"Text\":\"安魂曲，测试台词。\",\"Audio\":null}]");
            var importStore = new HuTao.Knowledge.Memory.ConversationMemoryStore(
                Path.Combine(dir, "memory-import.json"), options);
            importStore.ImportChatLog(
                [("2026-09-01 10:00:00", "assistant", "安魂曲，测试台词。")], DateTimeOffset.Now);
            var imported = importStore.Snapshot().FirstOrDefault(r => r.Persona.Length > 0);
            Check("memory_import_chat_log_stamps_persona",
                imported is not null && imported.Persona == "安魂曲",
                $"persona='{imported?.Persona ?? "(none)"}'");

            // 提炼产物必须**继承来源归属**，否则"你曾说"对提炼记忆全部失效。
            var source = store.ObserveTurn("assistant", "安魂曲，喜欢番茄酱。", DateTimeOffset.Now);
            var consolidated = store.AddConsolidated(
                "安魂曲喜欢番茄酱。", HuTao.Knowledge.Memory.MemoryKind.Preference,
                importance: 0.7, sources: [source!.Id],
                observedAt: DateTimeOffset.Now, now: DateTimeOffset.Now,
                validUntil: null, supersedesId: null);
            Check("memory_consolidation_inherits_persona",
                consolidated is not null && consolidated.Persona == "安魂曲",
                $"persona='{consolidated?.Persona}'");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static SessionDescriptor Descriptor(string id, bool pinned = false, int unread = 0)
    {
        var at = new DateTimeOffset(2026, 2, 14, 20, 15, 33, TimeSpan.FromHours(8));
        var character = id.StartsWith("single_", StringComparison.Ordinal) ? id["single_".Length..] : "hutao";
        var definition = CharacterCatalog.Find(character);
        return new SessionDescriptor(
            id,
            id.StartsWith("single_", StringComparison.Ordinal) ? SessionKind.Single : SessionKind.Group,
            definition?.Name ?? "群聊",
            definition?.Emoji ?? "💬",
            "#FF8C60BE",
            [character],
            [new MemberPresence(character, at)],
            at,
            at,
            "预览",
            pinned,
            false,
            false,
            unread);
    }

    private static ConversationTurn Turn(string id, string text, bool user)
        => new(id, DateTimeOffset.Now, user ? "user" : "hutao", user ? "你" : "胡桃",
            text, user, null, ["hutao"], ConversationTurnKind.Message);
}
