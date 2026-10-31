# Titanfall 2 Miles/Bink Audio bridge

This bridge decodes the BCF audio streams stored in Titanfall 2's MSTR files
(`tf2_miles_decode`, `tf2_miles_free`). It also exposes a small Miles event API
(`tf2_miles_events_*`, `tf2_miles_event_*`, implemented in `miles_events.inl`) that
loads the game's sound project and banks and plays named sound events. When a
session closes, the bank is released with `MilesBankUnload` if the runtime exports
it; older runtimes without that export skip the call.

The bridge dynamically loads `mileswin64.dll` and `binkawin64.dll` from the user's
local game installation. No Miles or Bink binary, header, import library, or source
code is included or redistributed by this project.

Build the x64 bridge with:

```powershell
.\build.ps1
```

The script finds the repository root from its own location, so it has no
machine-specific paths. The default output is
`game/mount/titanfall2/titanfall2_miles.dll`; override it with
`-OutputDirectory` when needed. The managed mounter passes the detected Steam
game directory to the bridge at runtime.
