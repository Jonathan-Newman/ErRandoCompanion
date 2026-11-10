using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ErRandoCompanion.Native;

/// <summary>
/// The C API. Every function takes and returns UTF-8 text; every returned string must be passed to
/// er_free. Replies are JSON: {"ok":true,"result":...} or {"ok":false,"error":"..."}.
///
///   char* er_init(const char* optionsJson)   options: dataDirectory, seedsDirectory, randomizerDirectory, readOwnProcess
///   char* er_call(const char* requestJson)   request: {"op":"...", ...arguments}, see Dispatch for the ops
///   void  er_free(char* reply)
///   void  er_shutdown(void)
///
/// Missing directories default to: Data beside this DLL, the app's Seeds folder, and the folder holding
/// this DLL's folder, as the mod sits inside the randomizer folder. Calls may come from any thread, one at a time.
/// Polling reads hundreds of flags, so call "poll" about once a second from a background thread, not every frame.
/// Nothing may throw out of these functions: inside the game an escaping exception would crash it.
/// </summary>
public static unsafe partial class Exports
{
    private static readonly object Gate = new();
    private static Session? _session;

    [UnmanagedCallersOnly(EntryPoint = "er_init")]
    public static byte* Init(byte* optionsJson) => (byte*)Guarded(() =>
    {
        var options = string.IsNullOrWhiteSpace(Read(optionsJson))
            ? new SessionOptions(null, null, null, false)
            : JsonSerializer.Deserialize(Read(optionsJson)!, NativeJsonContext.Default.SessionOptions)!;
        var here = ModuleDirectory();

        _session?.Dispose();
        _session = null;
        _session = new Session(
            options.DataDirectory ?? Path.Combine(here, "Data"),
            options.SeedsDirectory ?? DefaultSeedsDirectory(),
            options.RandomizerDirectory ?? Path.GetDirectoryName(here),
            options.ReadOwnProcess);
        return Ok(_session.Summary(), NativeJsonContext.Default.Summary);
    });

    [UnmanagedCallersOnly(EntryPoint = "er_call")]
    public static byte* Call(byte* requestJson) => (byte*)Guarded(() =>
    {
        var session = _session ?? throw new SessionException("Call er_init first.");
        using var request = JsonDocument.Parse(Read(requestJson) ?? "");
        return Dispatch(session, request.RootElement);
    });

    [UnmanagedCallersOnly(EntryPoint = "er_free")]
    public static void Free(byte* reply) => NativeMemory.Free(reply);

    [UnmanagedCallersOnly(EntryPoint = "er_shutdown")]
    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                _session?.Dispose();
            }
            catch
            {
                // Shutting down regardless.
            }
            _session = null;
        }
    }

    internal static nint Dispatch(Session session, JsonElement request)
    {
        var op = Text(request, "op");
        var context = NativeJsonContext.Default;
        return op switch
        {
            "listSeeds" => Ok(session.SeedNames(), context.IReadOnlyListString),
            "openSeed" => Ok(session.OpenSeed(Text(request, "name")), context.Summary),
            "deleteSeed" => Ok(session.DeleteSeed(Text(request, "name")), context.Boolean),
            "backups" => Ok(session.Backups(), context.IReadOnlyListBackupInfo),
            "goalKinds" => Ok(Session.GoalKinds(), context.IReadOnlyListGoalKindInfo),
            "setGoal" => Ok(session.SetGoal(Text(request, "kind"), OptionalInt(request, "count")), context.Summary),
            "bossChoices" => Ok(session.BossChoices(), context.IReadOnlyListBossChoiceInfo),
            "unlocks" => Ok(session.Unlocks(), context.IReadOnlyListUnlockInfo),
            "history" => Ok(session.History(), context.IReadOnlyListHistoryInfo),
            "found" => Ok(session.Found(), context.IReadOnlyListFoundInfo),
            "regions" => Ok(session.Regions(), context.IReadOnlyListRegionInfo),
            "report" => Ok(session.Report(OptionalText(request, "name")), context.ReportInfo),
            "toggleSkip" => Ok(session.ToggleSkip(Text(request, "id")), context.Summary),
            "skipNearest" => Ok(session.SkipNearest(), context.Summary),
            "toggleGoalBoss" => Ok(session.ToggleGoalBoss(Text(request, "id")), context.Summary),
            "restoreBackup" => Ok(session.RestoreBackup(Text(request, "name"), Text(request, "id")), context.Summary),
            "loadSpoilerLog" => Ok(session.LoadSpoilerLog(Text(request, "path")), context.Summary),
            "openNewestSeed" => Ok(session.OpenNewestSeed(), context.Summary),
            "summary" => Ok(session.Summary(), context.Summary),
            "checks" => Ok(session.Checks(OptionalText(request, "filter") ?? "all"), context.IReadOnlyListCheckInfo),
            "items" => Ok(session.Items(), context.IReadOnlyListItemInfo),
            "settings" => Ok(session.Settings(), context.IReadOnlyListSettingInfo),
            "setChecked" => Ok(session.SetChecked(Text(request, "id"), Bool(request, "checked")), context.Summary),
            "setTarget" => Ok(session.SetTarget(Text(request, "id"), OptionalInt(request, "number")), context.Summary),
            "setActiveTarget" => Ok(session.SetActiveTarget(OptionalInt(request, "number") ?? throw Missing("number")), context.Summary),
            "setItem" => Ok(session.SetItem(Text(request, "name"), OptionalInt(request, "count") ?? throw Missing("count")), context.Summary),
            "setSetting" => Ok(session.SetSetting(Text(request, "id"), Text(request, "value")), context.Summary),
            "poll" => Ok(session.Poll(OptionalInt(request, "nearby") ?? 0), context.PollInfo),
            _ => throw new SessionException($"Unknown op '{op}'."),
        };
    }

    // One lock for every call, and every failure turned into an error reply.
    private static nint Guarded(Func<nint> body)
    {
        lock (Gate)
        {
            try
            {
                return body();
            }
            catch (SessionException e)
            {
                return Error(e.Message);
            }
            catch (JsonException e)
            {
                return Error($"The request is not valid JSON: {e.Message}");
            }
            catch (Exception e)
            {
                return Error($"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static nint Ok<T>(T result, JsonTypeInfo<T> type)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, type);
            writer.WriteEndObject();
        }
        return ToNative(buffer.ToArray());
    }

    private static nint Error(string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return ToNative(buffer.ToArray());
    }

    private static nint ToNative(byte[] utf8)
    {
        var native = (byte*)NativeMemory.Alloc((nuint)utf8.Length + 1);
        utf8.CopyTo(new Span<byte>(native, utf8.Length));
        native[utf8.Length] = 0;
        return (nint)native;
    }

    private static string? Read(byte* utf8) => utf8 is null ? null : Marshal.PtrToStringUTF8((nint)utf8);

    private static string Text(JsonElement request, string name) =>
        OptionalText(request, name) ?? throw Missing(name);

    private static string? OptionalText(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static bool Bool(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Missing(name);

    private static SessionException Missing(string name) => new($"The request needs '{name}'.");

    private static string DefaultSeedsDirectory() =>
        Environment.GetEnvironmentVariable("ER_COMPANION_SEEDS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErRandoCompanion", "Seeds");

    // The DLL is loaded by the game, so the process's own folder says nothing about where the DLL sits.
    private static string ModuleDirectory()
    {
        const uint fromAddress = 0x4, unchangedRefCount = 0x2;
        delegate* unmanaged<byte*, byte*> anyExport = &Init;
        if (!GetModuleHandleExW(fromAddress | unchangedRefCount, (nint)anyExport, out var module))
            return AppContext.BaseDirectory;

        var path = new char[32768];
        fixed (char* buffer = path)
        {
            var length = GetModuleFileNameW(module, buffer, (uint)path.Length);
            return length == 0 ? AppContext.BaseDirectory : Path.GetDirectoryName(new string(path, 0, (int)length))!;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* fileName, uint size);
}
