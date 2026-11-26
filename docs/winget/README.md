# winget manifests

Files for listing Love My Shelf in the Windows Package Manager (winget, `microsoft/winget-pkgs`).

`manifests/l/LoveMyShelf/LoveMyShelf/1.0.7/` passes `winget validate`, but **don't submit 1.0.7**: that version updates its own files, which leaves winget's record out of date. Versions after 1.0.7 notice they were installed by winget (their folder is under `WinGet\Packages\LoveMyShelf.LoveMyShelf_…`) and update through `winget upgrade` instead.

For the first release after 1.0.7:

1. Copy the folder to the new version number and change `PackageVersion`, `ReleaseDate`, `ReleaseNotesUrl`, `InstallerUrl` (`…/download/vX.Y.Z/LoveMyShelf-Windows.zip`) and `InstallerSha256` (the `sha256:` digest GitHub shows for the ZIP).
2. Delete the `RequireExplicitUpgrade: true` line, so `winget upgrade --all` includes it.
3. Run `winget validate --manifest <folder>`, then open a pull request to `microsoft/winget-pkgs` with the same folder path under `manifests/`.

Why `ArchiveBinariesDependOnPath: true`: the exe loads `libmpv-2.dll`, `tools\`, `fonts\`, `upscale\` and `faces\` from its own folder. With winget's default symlink the program would look next to the link and videos wouldn't play, so the real install folder goes on PATH.
