# Titanfall 2 LZHAM bridge

This is a minimal native bridge around the MIT-licensed LZHAM alpha API used by
Titanfall 2 VPK chunks. It does not contain code from RSX or TFVPKTool.

The bridge uses the game's fixed `dict_size_log2 = 20` profile and exports only
`tf2_lzham_decompress`. Build it with:

```powershell
.\\build.ps1
```

The required Alpha8-compatible `lzham.h` and x64 static library are vendored in
`ThirdParty/lzham-alpha8`, so no machine-specific dependency path or environment
variable is required. This exact API revision includes the CRC32 interface used
by Titanfall 2 VPK chunks. See `ThirdParty/lzham-alpha8/LICENSE.txt` for the
MIT license and attribution.

The script finds the repository root from its own location. By default the
result is written to `game/mount/titanfall2/titanfall2_lzham.dll`, next to the
managed mount assembly. Override that with `-OutputDirectory` when needed. The
DLL is a local build artifact and is not committed.

LZHAM alpha is MIT-licensed; see the copyright notice in `lzham.h` of the
source distribution used to build the bridge.
