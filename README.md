# Cardboard Box Patcher

Windows x64 patcher for an existing Cardboard Box Modrinth installation.

[Download the patcher](https://github.com/KazzKamikaze/cardboard-box/releases/latest/download/Cardboard.Box.Patcher.exe) · [Releases and instructions](https://github.com/KazzKamikaze/cardboard-box/releases/latest)

Import the supplied base `.mrpack` once, close Minecraft and Modrinth, then open the patcher and choose the game folder containing `mods` and `options.txt`. Approved updates are checked on opening and every 15 minutes while the patcher remains open. Automatic installation is enabled by default and can be switched off.

The signed release feed pins file sizes and SHA-512 hashes. The patcher stages changed files before installation, retains backups, recovers interrupted file transactions and supports rollback. It preserves worlds, player controls, maps, Voxy caches and personal graphics preferences. It does not terminate running applications or modify launcher accounts/databases.

Publisher downloads are fetched directly; custom pack assets use a cached release bundle. Four custom third-party base binaries are verified in place rather than mirrored publicly. The base import remains necessary. Minecraft/NeoForge version changes require a new import. Read-only launcher-managed files are rejected before patching.

See [START-HERE.txt](START-HERE.txt) for usage and limitations, and [DEVELOPMENT.txt](DEVELOPMENT.txt) for build, signing and publishing details. A self-contained executable is attached to releases; users do not need the .NET runtime installed separately.

Validation includes disposable transaction/failure tests and a full current-pack snapshot update and rollback. It does not establish a successful Minecraft launch on every recipient's launcher version.

The source repository and release source ZIP contain the public verification key only. Private release-signing keys are never distributed. Component licenses remain with their original authors.
