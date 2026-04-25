using System.Text;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Reads the full Steam owned-game list from local binary files without any network access:
///   licensecache  → owned package IDs (Valve PRNG XOR + minimal protobuf)
///   packageinfo.vdf → app IDs for those packages (SteamKit2 binary VDF)
///   appinfo.vdf   → app names (SteamKit2 binary VDF)
/// </summary>
internal static class SteamLocalLibraryReader
{
    // Returns (appId → (name, appType)) for every owned app. appType is e.g. "game","Tool","DLC".
    internal static Dictionary<uint, (string name, string appType)> ReadOwnedApps(string steamRoot, long steamId64)
        => ReadOwnedAppsCore(steamRoot, steamId64, debug: false);

    internal static Dictionary<uint, (string name, string appType)> ReadOwnedAppsDebug(string steamRoot, long steamId64)
        => ReadOwnedAppsCore(steamRoot, steamId64, debug: true);

    private static Dictionary<uint, (string name, string appType)> ReadOwnedAppsCore(string steamRoot, long steamId64, bool debug)
    {
        long steam32 = steamId64 - 76561197960265728L;
        var userdata = Path.Combine(steamRoot, "userdata", steam32.ToString());

        var ownedPackages = ReadLicenseCache(userdata, (int)steam32);
        if (debug) Console.WriteLine($"  Packages from licensecache: {ownedPackages.Count}");
        if (ownedPackages.Count == 0) return [];

        var appIds = ReadPackageInfo(steamRoot, ownedPackages);
        if (debug) Console.WriteLine($"  App IDs from packageinfo: {appIds.Count} (sample: {string.Join(", ", appIds.Take(5))})");
        if (appIds.Count == 0) return [];

        var entries = ReadAppInfo(steamRoot, appIds);
        if (debug)
        {
            Console.WriteLine($"  Names from appinfo: {entries.Count}");
            var gameCount = entries.Count(e => e.Value.appType.Equals("game", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  Games (type=game): {gameCount}");
        }
        return entries;
    }

    // ── Step 1: licensecache → package IDs ───────────────────────────────────

    private static HashSet<uint> ReadLicenseCache(string userdata, int steam32)
    {
        var path = Path.Combine(userdata, "config", "licensecache");
        if (!File.Exists(path)) return [];
        try
        {
            var encrypted = File.ReadAllBytes(path);
            if (encrypted.Length < 8) return [];

            var decrypted = ValvePrng.Decrypt(steam32, encrypted);
            // strip last 4 bytes (checksum)
            return ParseLicenseProto(decrypted.AsSpan(0, decrypted.Length - 4));
        }
        catch { return []; }
    }

    // Minimal protobuf parse: CMsgClientLicenseList.licenses[].package_id (field 1, uint32)
    private static HashSet<uint> ParseLicenseProto(ReadOnlySpan<byte> data)
    {
        var result = new HashSet<uint>();
        int pos = 0;

        while (pos < data.Length)
        {
            // read tag varint
            if (!ReadVarint(data, ref pos, out ulong tag)) break;
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 7);

            if (fieldNum == 2 && wireType == 2)
            {
                // repeated License message (field 2 in CMsgClientLicenseList)
                if (!ReadVarint(data, ref pos, out ulong msgLen)) break;
                int end = pos + (int)msgLen;
                if (end > data.Length) break;
                ParseLicenseMessage(data[pos..end], result);
                pos = end;
            }
            else
            {
                // skip unknown field
                if (!SkipField(data, ref pos, wireType)) break;
            }
        }
        return result;
    }

    private static void ParseLicenseMessage(ReadOnlySpan<byte> msg, HashSet<uint> result)
    {
        int pos = 0;
        while (pos < msg.Length)
        {
            if (!ReadVarint(msg, ref pos, out ulong tag)) break;
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 7);

            if (fieldNum == 1 && wireType == 0) // package_id: uint32
            {
                if (!ReadVarint(msg, ref pos, out ulong pkgId)) break;
                result.Add((uint)pkgId);
            }
            else
            {
                if (!SkipField(msg, ref pos, wireType)) break;
            }
        }
    }

    private static bool ReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift >= 64) return false;
        }
        return false;
    }

    private static bool SkipField(ReadOnlySpan<byte> data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                while (pos < data.Length && (data[pos++] & 0x80) != 0) { }
                return true;
            case 1: pos += 8; return pos <= data.Length;  // 64-bit
            case 2:                                         // length-delimited
                if (!ReadVarint(data, ref pos, out ulong len)) return false;
                pos += (int)len;
                return pos <= data.Length;
            case 5: pos += 4; return pos <= data.Length;  // 32-bit
            default: return false;
        }
    }

    // ── Step 2: packageinfo.vdf → app IDs ────────────────────────────────────

    private static HashSet<uint> ReadPackageInfo(string steamRoot, HashSet<uint> ownedPackages)
    {
        var path = Path.Combine(steamRoot, "appcache", "packageinfo.vdf");
        if (!File.Exists(path)) return [];
        var result = new HashSet<uint>();

        try
        {
            using var f = File.OpenRead(path);
            f.ReadExactly(stackalloc byte[8]); // header: version (4) + universe (4)

            Span<byte> u32buf = stackalloc byte[4];
            while (f.Position < f.Length - 4)
            {
                f.ReadExactly(u32buf);
                uint pkgId = BitConverter.ToUInt32(u32buf);
                if (pkgId == 0xFFFFFFFF) break;

                f.Seek(32, SeekOrigin.Current); // SHA1(20) + change_number(4) + token(8)

                // Parse binary VDF and collect appids if this package is owned
                var data = ReadBinaryVdf(f);
                if (!ownedPackages.Contains(pkgId)) continue;

                // Structure: {pkgIdStr: {appids: {key: appid, ...}}}
                foreach (var outer in data.Values)
                {
                    if (outer is Dictionary<string, object> outerDict &&
                        outerDict.TryGetValue("appids", out var appidsObj) &&
                        appidsObj is Dictionary<string, object> appids)
                    {
                        foreach (var v in appids.Values)
                            if (v is uint aid) result.Add(aid);
                    }
                }
            }
        }
        catch { /* non-fatal */ }

        return result;
    }

    // ── Step 3: appinfo.vdf → names ──────────────────────────────────────────

    private static Dictionary<uint, (string name, string appType)> ReadAppInfo(string steamRoot, HashSet<uint> targetIds)
    {
        var path = Path.Combine(steamRoot, "appcache", "appinfo.vdf");
        if (!File.Exists(path)) return [];
        var result = new Dictionary<uint, (string name, string appType)>();

        try
        {
            using var f = File.OpenRead(path);
            Span<byte> u32buf = stackalloc byte[4];
            Span<byte> u64buf = stackalloc byte[8];

            f.ReadExactly(u32buf);
            uint rawMagic = BitConverter.ToUInt32(u32buf);
            int version = (int)(rawMagic & 0xFF);
            f.Seek(4, SeekOrigin.Current); // universe

            // V41+: string table at end of file; keys are uint32 indices into it
            string[]? stringTable = null;
            if (version >= 41)
            {
                f.ReadExactly(u64buf);
                long strtablePos = (long)BitConverter.ToUInt64(u64buf);
                long savedPos = f.Position;
                f.Seek(strtablePos, SeekOrigin.Begin);
                stringTable = ReadStringTable(f);
                f.Seek(savedPos, SeekOrigin.Begin);
            }

            // Per-entry header size (bytes after the 4-byte appId)
            int headerExtra = 0;
            if (version >= 36) headerExtra += 4;  // size
            headerExtra += 8;                      // info_state + last_updated
            if (version >= 38) headerExtra += 28;  // access_token(8) + sha1(20)
            if (version >= 36) headerExtra += 4;   // change_number
            if (version >= 40) headerExtra += 20;  // binary_sha1

            while (f.Position < f.Length - 4)
            {
                f.ReadExactly(u32buf);
                uint appId = BitConverter.ToUInt32(u32buf);
                if (appId == 0) break;

                if (headerExtra > 0) f.Seek(headerExtra, SeekOrigin.Current);

                if (targetIds.Contains(appId))
                {
                    var data = stringTable != null
                        ? ReadBinaryVdfV41(f, stringTable)
                        : ReadBinaryVdf(f);

                    string? name = null;
                    string? appType = null;
                    // V41 wraps everything under "appinfo" key; older versions use "common" directly
                    if (TryGetNested(data, out name, "appinfo", "common", "name"))
                        TryGetNested(data, out appType, "appinfo", "common", "type");
                    else if (TryGetNested(data, out name, "common", "name"))
                        TryGetNested(data, out appType, "common", "type");

                    if (!string.IsNullOrEmpty(name))
                        result[appId] = (name!, appType ?? "");
                }
                else
                {
                    if (stringTable != null)
                        SkipBinaryVdfIterativeV41(f);
                    else
                        SkipBinaryVdfIterative(f);
                }
            }
        }
        catch { /* non-fatal */ }

        return result;
    }

    private static string[] ReadStringTable(Stream f)
    {
        Span<byte> u32buf = stackalloc byte[4];
        f.ReadExactly(u32buf);
        int count = (int)BitConverter.ToUInt32(u32buf);
        var strings = new string[count];
        for (int i = 0; i < count; i++)
            strings[i] = ReadCString(f);
        return strings;
    }

    private static Dictionary<string, object> ReadBinaryVdfV41(Stream f, string[] table, int depth = 0)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (depth > 16) { SkipBinaryVdfIterativeV41(f); return result; }

        Span<byte> u32buf = stackalloc byte[4];
        Span<byte> u64buf = stackalloc byte[8];

        while (f.Position < f.Length)
        {
            int typeByte = f.ReadByte();
            if (typeByte < 0 || typeByte == 0x08) break;

            // V41: key is uint32 index into string table
            f.ReadExactly(u32buf);
            uint keyIdx = BitConverter.ToUInt32(u32buf);
            string key = keyIdx < (uint)table.Length ? table[keyIdx] : keyIdx.ToString();

            switch (typeByte)
            {
                case 0x00: // nested dict
                    result[key] = ReadBinaryVdfV41(f, table, depth + 1);
                    break;
                case 0x01: // string value — still null-terminated inline in V41
                    result[key] = ReadCString(f);
                    break;
                case 0x02: // uint32
                    f.ReadExactly(u32buf);
                    result[key] = BitConverter.ToUInt32(u32buf);
                    break;
                case 0x03: // float32
                    f.Seek(4, SeekOrigin.Current);
                    break;
                case 0x07: // uint64
                    f.ReadExactly(u64buf);
                    result[key] = BitConverter.ToUInt64(u64buf);
                    break;
                case 0x0A: // int32 (color)
                    f.Seek(4, SeekOrigin.Current);
                    break;
                default:
                    return result;
            }
        }
        return result;
    }

    private static void SkipBinaryVdfIterativeV41(Stream f)
    {
        int depth = 1;
        while (f.Position < f.Length && depth > 0)
        {
            int t = f.ReadByte();
            if (t < 0) break;
            if (t == 0x08) { depth--; continue; }
            f.Seek(4, SeekOrigin.Current); // key: 4-byte index in V41
            switch (t)
            {
                case 0x00: depth++; break;
                case 0x01: ReadCString(f); break; // value still null-terminated
                case 0x02: case 0x03: case 0x0A: f.Seek(4, SeekOrigin.Current); break;
                case 0x07: f.Seek(8, SeekOrigin.Current); break;
                default: return;
            }
        }
    }

    private static bool TryGetNested(Dictionary<string, object> dict, out string? value, params string[] keys)
    {
        value = null;
        object? cur = dict;
        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (cur is not Dictionary<string, object> d || !d.TryGetValue(keys[i], out cur))
                return false;
        }
        if (cur is Dictionary<string, object> last && last.TryGetValue(keys[^1], out var v))
        {
            value = v as string;
            return value != null;
        }
        return false;
    }

    // ── Binary VDF reader (minimal) ───────────────────────────────────────────

    private static Dictionary<string, object> ReadBinaryVdf(Stream f, int depth = 0)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (depth > 16) { SkipBinaryVdfIterative(f); return result; }

        Span<byte> u32buf = stackalloc byte[4];
        Span<byte> u64buf = stackalloc byte[8];

        while (f.Position < f.Length)
        {
            int typeByte = f.ReadByte();
            if (typeByte < 0 || typeByte == 0x08) break;

            string key = ReadCString(f);

            switch (typeByte)
            {
                case 0x00: // nested dict
                    result[key] = ReadBinaryVdf(f, depth + 1);
                    break;
                case 0x01: // string
                    result[key] = ReadCString(f);
                    break;
                case 0x02: // uint32
                    f.ReadExactly(u32buf);
                    result[key] = BitConverter.ToUInt32(u32buf);
                    break;
                case 0x03: // float32
                    f.Seek(4, SeekOrigin.Current);
                    break;
                case 0x07: // uint64
                    f.ReadExactly(u64buf);
                    result[key] = BitConverter.ToUInt64(u64buf);
                    break;
                case 0x0A: // int32 (color)
                    f.Seek(4, SeekOrigin.Current);
                    break;
                default:
                    return result; // unknown type, bail
            }
        }
        return result;
    }

    private static void SkipBinaryVdf(Stream f, int depth = 0) => SkipBinaryVdfIterative(f);

    private static void SkipBinaryVdfIterative(Stream f)
    {
        // Iterative traversal using a nesting depth counter
        int depth = 1;
        while (f.Position < f.Length && depth > 0)
        {
            int t = f.ReadByte();
            if (t < 0) break;
            if (t == 0x08) { depth--; continue; }
            ReadCString(f); // key
            switch (t)
            {
                case 0x00: depth++; break;
                case 0x01: ReadCString(f); break;
                case 0x02: case 0x03: case 0x0A: f.Seek(4, SeekOrigin.Current); break;
                case 0x07: f.Seek(8, SeekOrigin.Current); break;
                default: return;
            }
        }
    }

    private static string ReadCString(Stream f)
    {
        var bytes = new List<byte>(32);
        int b;
        while ((b = f.ReadByte()) > 0) bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

// ── Valve PRNG (direct port from Depressurizer / SteamLibraryManager reference) ──

internal static class ValvePrng
{
    private const int NTAB = 32;
    private const int IA   = 16807;
    private const int IM   = 2147483647;
    private const int IQ   = 127773;
    private const int IR   = 2836;
    private static readonly int NDIV = 1 + (IM - 1) / NTAB;

    internal static byte[] Decrypt(int seed, byte[] data)
    {
        // State
        int idum = seed >= 0 ? -seed : seed;
        int iy = 0;
        int[] iv = new int[NTAB];

        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ RandomInt(ref idum, ref iy, iv, 32, 126));
        return result;
    }

    // Generate next raw value, advancing state.
    private static int Generate(ref int idum, ref int iy, int[] iv)
    {
        if (idum <= 0 || iy == 0)
        {
            idum = (-idum < 1) ? 1 : -idum;
            for (int j = NTAB + 7; j >= 0; j--)
            {
                int k = idum / IQ;
                idum = IA * (idum - k * IQ) - IR * k;
                if (idum < 0) idum += IM;
                if (j < NTAB) iv[j] = idum;
            }
            iy = iv[0];
        }
        {
            int k = idum / IQ;
            idum = IA * (idum - k * IQ) - IR * k;
            if (idum < 0) idum += IM;
        }
        int j2 = iy / NDIV;
        if (j2 < 0 || j2 >= NTAB) j2 = (j2 % NTAB + NTAB) % NTAB;
        iy = iv[j2];
        iv[j2] = idum;
        return iy;
    }

    // Random int in [lo, hi] (inclusive), matching Python _random_int exactly.
    private static int RandomInt(ref int idum, ref int iy, int[] iv, int lo, int hi)
    {
        int range = hi - lo + 1;
        if (range <= 1) return lo;
        // mx = 0x7FFFFFFF - ((0x7FFFFFFF + 1) % range)
        // = int.MaxValue - ((int.MaxValue % range + 1) % range)
        int mx = IM - ((IM % range + 1) % range);
        int n;
        do { n = Generate(ref idum, ref iy, iv); } while (n > mx);
        return lo + (n % range);
    }
}
