using System.Buffers;
using System.Runtime.InteropServices;

namespace LingDongDao;

// KuGou keeps the timeline as UTF-16 text (MM:SS/MM:SS) in its process.
// The addresses are discovered once per track and rechecked on later polls.
internal sealed class KugouProgressReader
{
    private const int ChunkSize = 1024 * 1024;
    private const int MaximumCandidates = 160;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private readonly List<long> _candidates = new();
    private readonly HashSet<long> _active = new();
    private readonly Dictionary<long, int> _lastPositions = new();
    private int _processId;
    private string? _track;
    private DateTime _lastScanUtc;

    public (TimeSpan Position, TimeSpan Duration) Read(int processId, string track)
    {
        if (processId != _processId || track != _track)
        {
            _processId = processId;
            _track = track;
            _candidates.Clear();
            _active.Clear();
            _lastPositions.Clear();
            _lastScanUtc = DateTime.MinValue;
        }
        if (_candidates.Count == 0 && DateTime.UtcNow - _lastScanUtc > TimeSpan.FromSeconds(10))
        {
            _lastScanUtc = DateTime.UtcNow;
            _candidates.AddRange(Scan(processId));
        }
        if (_candidates.Count == 0) return default;
        IntPtr handle = OpenProcess(ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero) return default;
        try
        {
            var readings = new List<(long Address, int Position, int Duration)>();
            var buffer = new byte[48];
            foreach (long address in _candidates)
            {
                if (!ReadProcessMemory(handle, new IntPtr(address), buffer, buffer.Length, out IntPtr read) ||
                    read.ToInt64() < 16 ||
                    !TryParsePair(buffer.AsSpan(0, (int)read.ToInt64()), out int position, out int duration))
                    continue;
                readings.Add((address, position, duration));
            }
            if (readings.Count == 0)
            {
                _candidates.Clear();
                _active.Clear();
                return default;
            }
            var growing = new List<(long Address, int Position, int Duration)>();
            foreach (var reading in readings)
            {
                if (_lastPositions.TryGetValue(reading.Address, out int previous) &&
                    reading.Position > previous) growing.Add(reading);
                _lastPositions[reading.Address] = reading.Position;
            }
            if (growing.Count > 0)
            {
                _active.Clear();
                foreach (var reading in growing) _active.Add(reading.Address);
            }
            var selection = readings.Where(item => _active.Contains(item.Address)).ToArray();
            var best = (selection.Length > 0 ? selection : readings.ToArray())
                .MaxBy(item => item.Position);
            return (TimeSpan.FromSeconds(best.Position), TimeSpan.FromSeconds(best.Duration));
        }
        finally { CloseHandle(handle); }
    }

    private static List<long> Scan(int processId)
    {
        var candidates = new List<long>();
        IntPtr handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
        if (handle == IntPtr.Zero) return candidates;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long address = 0x10000;
            nuint infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            while (address < 0x7FFF_FFFF_0000)
            {
                if (VirtualQueryEx(handle, new IntPtr(address), out var info, infoSize) == 0) break;
                long baseAddress = info.BaseAddress.ToInt64();
                long regionSize = (long)info.RegionSize;
                if (regionSize <= 0 || baseAddress + regionSize <= address) break;
                bool readable = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                                (info.Protect & 0x01) == 0 && (info.Protect & 0xFF) != 0;
                if (readable && regionSize < 2L * 1024 * 1024 * 1024)
                {
                    for (long offset = 0; offset < regionSize; offset += ChunkSize)
                    {
                        int count = (int)Math.Min(ChunkSize, regionSize - offset);
                        if (!ReadProcessMemory(handle, new IntPtr(baseAddress + offset), buffer, count,
                                out IntPtr bytesRead) || bytesRead.ToInt64() < 16) continue;
                        ScanChunk(buffer.AsSpan(0, (int)bytesRead.ToInt64()), baseAddress + offset, candidates);
                        if (candidates.Count >= MaximumCandidates) return candidates;
                    }
                }
                address = baseAddress + regionSize;
            }
            return candidates;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            CloseHandle(handle);
        }
    }

    private static void ScanChunk(ReadOnlySpan<byte> bytes, long baseAddress, List<long> candidates)
    {
        for (int index = 2; index + 16 <= bytes.Length; index += 2)
        {
            if (bytes[index] != ':' || bytes[index + 1] != 0 || bytes[index - 1] != 0 ||
                bytes[index - 2] is < (byte)'0' or > (byte)'9') continue;
            int start = index - 2;
            for (int digits = 1; digits < 3 && start >= 2 && bytes[start - 1] == 0 &&
                 bytes[start - 2] is >= (byte)'0' and <= (byte)'9'; digits++) start -= 2;
            if (start >= 2 && bytes[start - 1] == 0 &&
                bytes[start - 2] is >= (byte)'0' and <= (byte)'9') continue;
            if (TryParsePair(bytes.Slice(start, Math.Min(40, bytes.Length - start)), out _, out _))
                candidates.Add(baseAddress + start);
            if (candidates.Count >= MaximumCandidates) return;
        }
    }

    internal static bool TryParsePair(ReadOnlySpan<byte> bytes, out int position, out int duration)
    {
        position = duration = 0;
        if (!TryParseTime(bytes, 0, out position, out int offset)) return false;
        if (offset + 1 >= bytes.Length || bytes[offset] != '/' || bytes[offset + 1] != 0) return false;
        if (!TryParseTime(bytes, offset + 2, out duration, out int end)) return false;
        if (duration is < 30 or > 10800 || position > duration) return false;
        return end + 1 >= bytes.Length || bytes[end + 1] != 0 ||
               bytes[end] is < (byte)'0' or > (byte)'9';
    }

    private static bool TryParseTime(ReadOnlySpan<byte> bytes, int offset, out int seconds, out int end)
    {
        seconds = 0;
        end = offset;
        int minutes = 0, digits = 0;
        while (digits < 3 && offset + 1 < bytes.Length && bytes[offset + 1] == 0 &&
               bytes[offset] is >= (byte)'0' and <= (byte)'9')
        {
            minutes = minutes * 10 + bytes[offset] - '0';
            offset += 2;
            digits++;
        }
        if (digits == 0 || offset + 5 >= bytes.Length || bytes[offset] != ':' || bytes[offset + 1] != 0 ||
            bytes[offset + 3] != 0 || bytes[offset + 5] != 0 ||
            bytes[offset + 2] is < (byte)'0' or > (byte)'9' ||
            bytes[offset + 4] is < (byte)'0' or > (byte)'9') return false;
        int remainder = (bytes[offset + 2] - '0') * 10 + bytes[offset + 4] - '0';
        if (remainder > 59) return false;
        seconds = minutes * 60 + remainder;
        end = offset + 6;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
        int length, out IntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address,
        out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
