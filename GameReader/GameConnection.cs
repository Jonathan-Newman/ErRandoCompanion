using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ErRandoCompanion.GameReader;

public enum GameStatus
{
    /// <summary>The game is not running.</summary>
    NotRunning,

    /// <summary>Easy Anti-Cheat is active, so the reader stays away from the game.</summary>
    AntiCheatActive,

    /// <summary>The game is running but its memory could not be opened.</summary>
    AccessDenied,

    /// <summary>Attached, but the flag manager is not available yet.</summary>
    Loading,

    /// <summary>Flags can be read.</summary>
    Connected,

    /// <summary>
    /// The game's code does not contain the patterns the reader looks for, which means a game version the
    /// signatures do not fit (usually after a game update). Code does not change while the game runs, so this lasts
    /// until the game restarts.
    /// </summary>
    Unsupported,
}

/// <param name="Player">Null when the position cannot be read: during loading, or when the offsets do not fit this game version.</param>
/// <param name="PositionSupported">False when the pattern for the player's position is missing from this game version.</param>
public readonly record struct PollResult(GameStatus Status, IReadOnlySet<uint> FlagsOn, PlayerPosition? Player = null, bool PositionSupported = true);

/// <summary>
/// Owns the link to the game: finds it, recovers when it closes or restarts, and
/// reads flags and the player's position. It only ever opens the game for reading. Not thread-safe.
/// </summary>
/// <param name="readOwnProcess">
/// True inside the game, as the mod: reads this process rather than looking for the game. Reads still go
/// through ReadProcessMemory, so a stale pointer fails the read instead of crashing the game.
/// </param>
public sealed partial class GameConnection(GameSignatures signatures, bool readOwnProcess = false) : IDisposable
{
    private const string GameProcess = "eldenring";
    private static readonly string[] AntiCheatProcesses = ["EasyAntiCheat_EOS", "EasyAntiCheat", "start_protected_game"];
    private const int ScanChunkSize = 1 << 20;
    private static readonly IReadOnlySet<uint> NoFlags = new HashSet<uint>();

    private readonly PlayerOffsets? _playerOffsets = signatures.ParsePlayerOffsets();
    private Process? _process;
    private ProcessMemory? _memory;
    private EventFlagReader? _flags;
    private PlayerReader? _player;
    private InventoryReader? _inventory;
    private long? _gameDataMan;
    private bool _unsupported;
    private bool _positionUnsupported;

    public PollResult Poll(IEnumerable<uint> flagIds)
    {
        if (!readOwnProcess && IsAntiCheatRunning())
        {
            Disconnect();
            return new PollResult(GameStatus.AntiCheatActive, NoFlags);
        }

        try
        {
            return PollAttached(flagIds);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // The game closed between steps; the next poll starts over.
            Disconnect();
            return new PollResult(GameStatus.NotRunning, NoFlags);
        }
    }

    public void Dispose() => Disconnect();

    private PollResult PollAttached(IEnumerable<uint> flagIds)
    {
        if (_process is { HasExited: true })
            Disconnect();

        if (_process is null)
        {
            var status = TryAttach();
            if (status != GameStatus.Connected)
                return new PollResult(status, NoFlags);
        }

        if (_unsupported)
            return new PollResult(GameStatus.Unsupported, NoFlags);
        if (_flags is null && !TryFindAddresses())
            return new PollResult(_unsupported ? GameStatus.Unsupported : GameStatus.Loading, NoFlags);

        // All at once: each category's block is found once and read whole.
        var on = _flags!.ReadFlags(flagIds);
        if (on is null)
            return new PollResult(GameStatus.Loading, NoFlags);
        return new PollResult(GameStatus.Connected, on, _player?.Read(), !_positionUnsupported);
    }

    private static bool IsAntiCheatRunning()
    {
        foreach (var name in AntiCheatProcesses)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var process in found)
                process.Dispose();
            if (found.Length > 0)
                return true;
        }
        return false;
    }

    private GameStatus TryAttach()
    {
        var process = readOwnProcess ? Process.GetCurrentProcess() : Process.GetProcessesByName(GameProcess).FirstOrDefault();
        if (process is null)
            return GameStatus.NotRunning;

        var memory = ProcessMemory.Open(process.Id);
        if (memory is null)
        {
            process.Dispose();
            return GameStatus.AccessDenied;
        }

        _process = process;
        _memory = memory;
        return GameStatus.Connected;
    }

    // Both addresses come from one read of the game's code, which is large enough that reading it twice would be slow.
    private bool TryFindAddresses()
    {
        try
        {
            var module = _process!.MainModule;
            if (module is null)
                return false;

            var baseAddress = module.BaseAddress.ToInt64();
            var image = ReadImage(baseAddress, module.ModuleMemorySize);
            var flagManager = SignatureScanner.ResolveAddress(image, baseAddress, signatures.EventFlagManager!);
            if (flagManager is null)
            {
                // The code is all there from the start, so a pattern that is not in it never will be.
                _unsupported = true;
                return false;
            }

            _flags = new EventFlagReader(_memory!, flagManager.Value);
            if (signatures.WorldChrMan is { } worldChrMan && _playerOffsets is not null
                && SignatureScanner.ResolveAddress(image, baseAddress, worldChrMan) is { } manager)
            {
                _player = new PlayerReader(_memory!, manager, _playerOffsets);
            }
            else
            {
                _positionUnsupported = true;
            }
            if (signatures.GameDataMan is { } gameDataMan && SignatureScanner.ResolveAddress(image, baseAddress, gameDataMan) is { } dataMan)
            {
                _gameDataMan = dataMan;
                _inventory = new InventoryReader(_memory!, dataMan);
            }
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // Read in chunks and leave gaps as zeros, as some pages of the image can be unreadable.
    private byte[] ReadImage(long baseAddress, int size)
    {
        var image = new byte[size];
        var chunk = new byte[ScanChunkSize];
        for (var offset = 0; offset < size; offset += ScanChunkSize)
        {
            var count = Math.Min(ScanChunkSize, size - offset);
            if (_memory!.TryRead(baseAddress + offset, chunk, count))
                Array.Copy(chunk, 0, image, offset, count);
        }
        return image;
    }

    /// <summary>What the player holds now; null when the game is not connected or the inventory cannot be found.</summary>
    /// <summary>
    /// The loaded character's death count (GameDataMan+0x94, from fromsoftware-rs's GameDataMan layout), or null when
    /// it cannot be read or does not look like a count.
    /// </summary>
    public uint? ReadDeathCount()
    {
        if (_memory is null || _gameDataMan is not { } address)
            return null;
        try
        {
            var buffer = new byte[8];
            if (!_memory.TryRead(address, buffer, 8))
                return null;
            var manager = BitConverter.ToInt64(buffer, 0);
            if (manager < 0x10000 || !_memory.TryRead(manager + 0x94, buffer, 4))
                return null;
            var deaths = BitConverter.ToUInt32(buffer, 0);
            return deaths < 1_000_000 ? deaths : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public InventorySnapshot? ReadInventory()
    {
        try
        {
            return _inventory?.Read();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void Disconnect()
    {
        _flags = null;
        _player = null;
        _inventory = null;
        _gameDataMan = null;
        // A restarted (perhaps updated) game gets a fresh look.
        _unsupported = false;
        _positionUnsupported = false;
        _memory?.Dispose();
        _memory = null;
        _process?.Dispose();
        _process = null;
    }

    private sealed partial class ProcessMemory : IMemoryReader, IDisposable
    {
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private readonly nint _handle;

        private ProcessMemory(nint handle) => _handle = handle;

        public static ProcessMemory? Open(int processId)
        {
            var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, processId);
            return handle == 0 ? null : new ProcessMemory(handle);
        }

        public bool TryRead(long address, byte[] buffer, int count) =>
            ReadProcessMemory(_handle, (nint)address, buffer, count, out var read) && read == count;

        public void Dispose() => CloseHandle(_handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nint size, out nint read);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
