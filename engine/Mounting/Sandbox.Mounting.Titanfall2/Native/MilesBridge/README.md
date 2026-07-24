# Titanfall 2 Miles/Bink Audio bridge

This bridge decodes the BCF audio streams stored in Titanfall 2's MSTR files. It
dynamically loads `mileswin64.dll` and `binkawin64.dll` from the user's local game
installation. No Miles or Bink binary, header, import library, or source code is
included or redistributed by this project.

Build the x64 bridge with:

```powershell
.\build.ps1
```

The output is `game/mount/titanfall2/titanfall2_miles.dll`. The managed mounter
passes the detected Steam game directory to the bridge at runtime.
