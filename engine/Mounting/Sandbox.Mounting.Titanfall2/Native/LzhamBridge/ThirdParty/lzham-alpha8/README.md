# Vendored LZHAM ABI package

`LzhamBridge` links this local package, so building the Titanfall 2 mount does
not depend on a developer-specific `LZHAM_ROOT` or any other external folder.

The package contains the exact LZHAM Alpha8-compatible headers and x64 static
library previously used by the mount. Its public API includes the Adler-32 and
CRC-32 output parameters required by Titanfall 2's compressed VPK data.

The source distribution available with the previous mount only contained these
headers and the prebuilt `liblzham_x64.lib`; it did not include the matching
implementation source. Do not replace this library with a different LZHAM
release unless its ABI (including `lzham_decompress_memory`) is verified
against Titanfall 2 assets.

License: MIT. See [LICENSE.txt](LICENSE.txt).
