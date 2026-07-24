# Titanfall 2 LZHAM bridge

This is a minimal native bridge around the MIT-licensed LZHAM alpha API used by
Titanfall 2 VPK chunks. It does not contain code from RSX or TFVPKTool.

The bridge uses the game's fixed `dict_size_log2 = 20` profile and exports only
`tf2_lzham_decompress`. Build it with:

```powershell
./build.ps1 -LzhamRoot 'path-to-lzham'
```

`LzhamRoot` must contain `include/lzham.h` and `lib/liblzham_x64.lib`. The
result is written to `game/mount/titanfall2/titanfall2_lzham.dll`, next to the
managed mount assembly. The DLL is a local build artifact and is not committed.

LZHAM alpha is MIT-licensed; see the copyright notice in `lzham.h` of the
source distribution used to build the bridge.
