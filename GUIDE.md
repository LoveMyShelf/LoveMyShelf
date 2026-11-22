# Love My Shelf guide

[한국어 설명서](GUIDE.ko.md) · [Back to README](README.md)

### What Love My Shelf does

Love My Shelf shows big, messy folders of photos, GIFs and videos as one large grid, so you can see what is in them at a glance without opening each file. When you find one you like, you can pick a picture or a single moment from a video and frame it as a tall 9:16 image. We call that image a **cover**. The tile then shows the cover, so you recognize it the next time you open the folder.

Your original files are never changed. Each cover is saved as a separate JPG in a folder you choose.

### Install and first run

1. Download **[LoveMyShelf-Windows.zip](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Windows.zip)**.
2. Right-click the ZIP, choose **Extract All…**, then **Extract**. Always extract the whole ZIP. Running the app from inside the ZIP does not work, because it needs the other files next to it.
3. Double-click **LoveMyShelf.exe** in the extracted folder.
4. Windows may show **"Windows protected your PC"**. This happens because the app is not signed with a paid certificate. Click **More info**, then **Run anyway**.
5. Choose **한국어** or **English**. You can change it later in **Guide & Options**.
6. Read the short welcome, then click **Let’s start**.
7. Love My Shelf asks where to save your covers. Choose or create a folder, for example `Pictures\Covers`.

Requirements: Windows 10 or 11 (64-bit). The .NET Framework 4.8 that Windows already includes is enough. Nothing else needs to be installed. FFmpeg for video covers is already in the ZIP.

### Supported files

- **Photos:** JPG, JPEG, JFIF, PNG, BMP, TIF, TIFF
- **Animated images:** GIF, WebP (including animated WebP)
- **Videos:** MP4, M4V, MOV, MKV, WEBM, AVI, WMV, MPG, MPEG, MTS, M2TS, TS, 3GP

### Guide & Options

Click **Guide & Options** at the top right, or press **F1** on any screen (it opens the part for the screen you're on).

- Pick a topic on the left to read about it in plain words. Shortcuts are drawn as keys and button names look like the buttons.
- Right below an explanation, a **Choose it right here** box holds that feature's settings, with every choice spelled out.
- A choice is saved and used the moment you click it. There is no Save button.
- **Default** marks the value Love My Shelf starts with.
- **All settings at a glance** lists every setting with its current value and a **Change** button.

### Light and dark

**Guide & Options → Screen, language, updates**: **Follow Windows** (default), **Light** or **Dark**. With Follow Windows, Love My Shelf changes along with Windows, even while it's open. Pictures keep a dark surround in both modes so they're easy to look at.

### The main window

- **Add folder** (or **Ctrl+O**) adds a folder. You can also drag a folder from File Explorer onto the window. Each folder appears as a tab at the top.
- Right-click a folder tab and choose **Hide folder tab** to remove it from the top bar. Your files are not touched.
- The **All / Unfinished / Finished** switch filters a folder: everything, only items without a cover, or only items that already have one.
- **Unfinished cover collection** gathers items without a cover from every visible folder tab, so you can work through them in one place.
- **Cover collection** shows all your saved covers from every folder in one grid, including several covers of the same original.
- The **Sort** button at the bottom right opens a menu upward, like File Explorer: **File name**, **Date modified**, **File size**, **File type** or **Cover work order**, then which end comes first (A → Z, newest first and so on). Picking one refreshes the grid in that order. **Shuffle**, at the very bottom, mixes the order until you pick a sort again.
- **Cover size** cycles the grid through four tile sizes.
- To rearrange the folder tabs, drag a tab left or right. Right-click a tab and choose **Open in new window** to open that folder in a new window. **Cover collection** and **Unfinished cover collection** always stay on the left. The order is remembered.
- **F5** refreshes the current view after you add or move files.
- **Search names** at the top right shows only files whose names contain what you type. **Esc** in the box clears it.
- Point at a tile to see its file name on a soft strip at the bottom, away from faces at the top.

### Browse like File Explorer

| Key | What it does |
| --- | --- |
| **←** **→** **↑** **↓** | Move between tiles (without opening) |
| **Enter** | Open the chosen tile big |
| **Home** / **End** | First / last tile |
| **Ctrl+click** / **Shift+click** | Add or remove one tile / choose a range |
| **Ctrl+A** | Choose every tile |
| **Ctrl+N** | Open another window (to drag files between two folders) |
| **Ctrl+Z** | Put the last move of files back |
| **Esc** | Clear the choice |
| **F2** | Rename |
| **Delete** | Move to the Recycle Bin (originals in a folder, cover files in Cover collection) |

- With several tiles chosen, right-click one of them to **Copy**, **Share**, **Hide the covers** or **Move to the Recycle Bin** all at once.
- Right-click a tile for **Rename**, **Open with default app**, **Open file location**, **Open cover file location** and **Details** (size, resolution, length, date, number of covers).
- **Move files to another folder:** press **Ctrl+N** (or click **New window**) to open a second window beside the first, open the destination folder's tab there, then drag the chosen tiles onto it. The window's edge lights up and says how many files will move. The covers go along with each file, so they still open the right original. Hold **Ctrl** while you let go to copy instead. If the destination already has a file with that name, Love My Shelf asks: **Replace** (the old file goes to the Recycle Bin), **Skip** or **Keep both** (the moved file gets a number after its name). **Ctrl+Z** puts the last move back.
- **Rename** keeps the extension and gives the same new name to the file's covers, upscaled copies and GIFs. A name that's already taken is refused.
- Tiles never move on their own. Saving a cover, hiding one, sending one to the Recycle Bin or hiding a folder tab leaves every other tile where it was (a removed tile leaves an empty slot). **F5**, **Shuffle**, or reopening the view tidies up.
- **Open cover folder** (bottom center) opens the folder of saved JPGs in File Explorer. If you save covers next to the originals, it opens the folder you are looking at.
- The counter at the bottom shows how many items are in the view and how many are finished.

### Make a cover from a photo

1. Double-click a photo to open it, then click **Make cover**.
2. A tall frame appears over the picture. The part inside the frame becomes the cover.
   - **Drag** the picture to move it.
   - **Roll the mouse wheel** to zoom in or out.
   - **Rotation**: drag the slider, or use the buttons to turn by 0.1° or 90° either way. **Reset** returns to 0°.
   - **Brightness**: drag the slider to brighten or darken. **Reset** returns to 0.
   - If snapping is on (**Guide & Options → Making a cover**), the frame sticks to the picture's edge when it comes close. Keep dragging to pull it free.
3. Click **Save cover**. The cover's large view opens right away. The JPG is saved at the full size of your crop in the highest quality. Empty black bands at the top or bottom are trimmed automatically. The tile in the grid changes to your new cover.
4. Press **Esc** to leave the editor without saving.

Example: to make a face fill the screen, zoom in until the top of the frame touches the top of the head, then move the picture so the face is centered.

### Make a cover from a video, GIF, or WebP

This works right after you unzip. The FFmpeg tools it needs are included in the `tools` folder.

1. Open the video and play it. Pause near the moment you want.
2. Click **Make cover**. Love My Shelf reads the frames around the paused time and shows them as a list.
3. Use the **Frame size** slider to make the thumbnails bigger or smaller. Switch to **All frames** to browse the whole clip.
4. Click the frame you want. The cover editor opens with that exact frame.
5. In the editor, use **Previous frame** and **Next frame** (**D** / **F**) to compare neighboring frames without leaving. The keys are the same as in the large view: **←** / **→** skip the seconds you chose (**Shift** for one second), and **E** / **W** / **Q** make it brighter, darker or normal. Frame the shot the same way as a photo and click **Save cover**.

Tip: in the player, press **D** or **F** to pause and step back or forward one frame at a time before you click **Make cover**.

### View, edit, and remake covers

- **Double-click** a tile with a cover to open the large view you chose in Guide & Options (the cover or the original). One click only chooses the tile; you can switch to one-click opening under Guide & Options → Large view.
- **Right-click** a tile to choose **View cover large** or **View original large**.
- Inside the large view, **View cover** and **View original** switch between the two.
- **Edit cover** opens the editor with your saved framing, so you can make small changes. For a video, the saved frame opens.
- **Add cover** in the large view (**Make another cover** when you right-click a tile) makes one more cover from the same original, for example from a different frame or with a tighter crop. The earlier cover stays. The folder shows one tile for each cover, side by side, right after the original's first tile. All of them belong to the same original, so Delete (the original goes to the Recycle Bin, and every tile of it disappears), Rename, moving to another folder and the file location work on the original once, from any of them. **Cover collection** shows every cover as its own tile too.
- **Play with default player** opens a video in your usual Windows video player.

### JPG covers and GIF covers

A still cover is a **JPG cover**. A moving cover is a **GIF cover**, even when you save it as WebP or MP4.

### Make a GIF cover

From a video, GIF or WebP cover you can make a short moving cover with the same crop, rotation and brightness. Start it from **Make GIF cover** in the cover editor (next to **Save cover**), in the cover's large view (next to **Upscale**), or in a tile's right-click menu.

1. The window shows a **scene strip** around the cover's moment (the white mark). How much it shows at first is set in **Guide & Options → GIF covers** (10 s by default).
2. Drag the two blue **handles** to the start and the end. **‹** / **›** next to **Start** and **End** under the strip, or **←** / **→** (for the last one you picked), move them one scene. One scene is the **scene step** in Guide & Options → Scenes from videos (0.2 s to start). Dragging a handle also lands on whole scenes.
3. Drag the pictures sideways to see other parts of the video. If you drag a handle to the strip's edge, the strip follows. **Back to the cover** jumps back to the cover's moment. There's no limit on the length.
4. Pick the **speed** (0.5× to 2×) and the **play style**: **Loop**, **Back and forth**, or **Backward**.
5. The **preview** at the top is the real file, so it's exactly what you'll get. While it's being made it shows its progress, and a thin line on the strip shows where it is playing. Click the preview or press **Space** to pause and play. Under the strip you see the length, the number of frames, the real size in pixels and the file size (in orange when very large).
6. **More choices** has smoothness (frames per second), height, picture quality (High, Normal, Smaller file) and file type (GIF, WebP or MP4). What these start as each time is set in **Guide & Options → GIF covers**, which also explains the differences between GIF, WebP and MP4. Click **Make** (or **Save JPG cover and make GIF cover** in the editor); the file made for the preview is saved right away.

The JPG cover and the GIF cover are both saved. In the grid the JPG cover shows first and the GIF cover plays when you point at it (you can change this in **Guide & Options → GIF covers**). In the large view it plays automatically; **View JPG cover** switches to the JPG cover and **View GIF cover** back.

### Large view and playback

| Key | What it does |
| --- | --- |
| **K** / **L** | Previous / next file |
| **Space** | Play or pause |
| **←** / **→** | Skip back / forward (5 s by default; change it in Guide & Options) |
| **D** / **F** | Pause and go one frame back / forward |
| **E** / **W** | Make playback 1% brighter / darker |
| **Q** | Reset playback brightness |
| **C** / **X** / **Z** | Faster / slower by 0.1× / normal speed |
| **F11** | Full screen on or off |
| **Esc** | Close the large view |

- The bottom bar has a timeline and **Volume**. **C** / **X** change the speed by 0.1× (0.2× to 4×), **Z** goes back to 1×.
- Changing the speed or brightness with keys shows a small note at the top left, like PotPlayer.
- Point at the timeline and a large picture of that moment appears, with no frame around it and the time written inside it at the bottom.
- In a window the bar always stays below the video, like PotPlayer, and never covers it. In full screen it floats over the video with a clear background when you point at the bottom.
- In full screen, move the pointer to the top edge to show the title bar, or to the bottom edge to show the timeline and buttons.
- **Double-click** the video to pause or resume.
- The large view fills the window height. Wide pictures may be cut at the sides. Black bands above and below a saved cover are hidden.

### Built-in video player

The ZIP includes `libmpv-2.dll`, the engine of the free mpv player. It plays high-resolution and VP9, AV1, and HEVC videos smoothly, which Windows' own player often cannot. If you delete this file, Love My Shelf falls back to Windows playback.

### Font

Love My Shelf shows its text in Pretendard, a free font made for Korean and English. The font files are in the `fonts` folder next to `LoveMyShelf.exe`, so nothing is installed on your PC. If the folder is missing, the app uses Segoe UI and Malgun Gothic instead.

### AI upscale (make small covers sharper)

If a saved cover is less than 1920 px tall, the cover's large view shows an **Upscale** button.

1. Click **Upscale**. An AI model (Swin2SR) enlarges the cover 4× in the background. You can keep using Love My Shelf.
2. The button shows the progress, for example **Upscaling 40%**. A small cover takes from about 30 seconds to a few minutes on a laptop CPU.
3. When it is done, a notice appears at the lower right. Click it, or click that cover's tile once, to open the comparison window.
4. The original and the upscaled cover appear side by side. Scroll to zoom and drag to move. Both pictures move together. Double-click to reset.
5. Click **Use this one** under the version you like. **Decide later** keeps the choice open.

Both JPGs stay in the cover folder. The upscaled file ends with `-upscaled.jpg`. **Compare upscale** reopens the comparison so you can change your mind. Remaking a cover removes the link to the upscaled file but does not delete it.

### Organize

- **Hide this cover in Love My Shelf** keeps the file but stops showing the cover: it leaves **Cover collection**, and the folder tile shows the original instead (an extra cover's own tile simply disappears from the folder). **Show this cover again** brings it back. If other covers of the same original are hidden, the folder tile's menu offers **Show hidden covers again**.
- **Move this cover file to the Recycle Bin** sends the cover JPG (and its upscaled copy and GIF, if any) to the Windows Recycle Bin and forgets that cover. The original stays where it is. When the cover also has a GIF cover, the menu says **Move the JPG and GIF cover files to the Recycle Bin** and adds **Move only the GIF cover file to the Recycle Bin**, which keeps the JPG cover (restore the GIF from the Recycle Bin and it comes back). The hide and show items then read **…the JPG and GIF covers…**, because they act on both.
- **Move original to Recycle Bin** deletes the original photo or video to the Windows Recycle Bin, so you can restore it from there.
- If you moved or renamed an original, the tile shows the saved preview. Use **Locate original** to point Love My Shelf to the new location.

### Copy, share and drag

Right-click a tile and choose **Copy** or **Share…**, or drag the tile into a chat window or a folder.

- **Copy** puts the files on the clipboard. Paste them with **Ctrl+V** in a chat window or a folder.
- **Share…** opens the Windows share window.
- What goes along is set in **Guide & Options → Sharing**: **Cover and original** (default), **Cover only**, or **Original only**. The Copy and Share menus also let you pick one of these just this once. A GIF made from the cover goes along with the cover.
- If an original video is bigger than the limit in Guide & Options (20 MB by default), Copy and Share ask whether to **Send it as it is** or send **Only about 10 seconds around the cover**. Dragging always hands over the file as it is.

Love My Shelf never uploads anything by itself. It only hands files to Windows, and you decide where they go.

### What you can set in Guide & Options

- **Browsing files**: the sort order and which end comes first (same as the **Sort** button).
- **Making a cover**: snapping to the picture's edges, and how much one wheel step zooms (3, 5, 7, 10 or 15%).
- **Saving covers**: where covers go (one cover folder, the original's folder, or a "Love My Shelf 표지" folder inside it) and the cover folder.
- **Scenes from videos**: how far ← and → skip (3, 5, 10 or 30 s).
- **GIF covers**: the starting file type, quality, pictures per second, height and length around the cover; the scene strip length; whether GIF covers move in the tiles.
- **Large view**: whether clicking a photo tile, or a video/GIF/WebP tile, opens the cover or the original.
- **Sharing**: what Copy, Share and dragging send, and above which size (10, 20, 50 or 100 MB) Love My Shelf asks first about a video.
- **Screen, language, updates**: light or dark, 한국어 or English, and new-version notices.

Values typed into an older version (such as 12 s) are kept and shown as they are.

### Where your data is

- **Covers:** ordinary JPG files (and GIF, WebP or MP4 if you made one) in the place chosen in Guide & Options.
- **App records** (folder list, cover framing, settings): `%LOCALAPPDATA%\LoveMyShelf\Folders`. Paste that into the File Explorer address bar to open it.
- **Originals:** left exactly where they were. Love My Shelf only reads them.
- The only network connection is the optional new-version check, which asks GitHub for the latest release number. No photos or personal information are sent.

Upgrading: extract the new ZIP over the old folder and replace the old files. Your covers and records are kept.

### FFmpeg for video covers

Picking a frame from a video uses `ffmpeg.exe` and `ffprobe.exe`. They are already in the `tools` folder next to `LoveMyShelf.exe`, together with the DLL files they need, so there is nothing to set up. Keep the `tools` folder where it is.

If you delete `tools`, Love My Shelf looks for FFmpeg on your `PATH` instead. To use a different FFmpeg build, replace the files in `tools` with your own `ffmpeg.exe` and `ffprobe.exe` (and their DLLs if it is a "shared" build).

### Troubleshooting

- **"Windows protected your PC"** → **More info → Run anyway**. The app is unsigned.
- **"Love My Shelf is already running."** → Only one copy runs at a time. Check the taskbar.
- **".exe was not found" when making a video cover** → The `tools` folder is missing or incomplete. Extract the whole ZIP again.
- **No Upscale button** → The cover is already 1920 px tall or more, or the `upscale` folder is missing next to `LoveMyShelf.exe`.
- **A video won't play or stutters** → Make sure `libmpv-2.dll` is next to `LoveMyShelf.exe`. **Play with default player** is another option.
- **"Original unavailable"** → The original was moved or renamed. Use **Locate original**.

### Build from source

On Windows, open PowerShell in the source folder and run:

```powershell
.\build.ps1
.\LoveMyShelf.exe
```

The build uses the C# compiler and WPF that ship with the Windows .NET Framework 4.x. No Visual Studio or NuGet is needed. For the optional parts, place `ffmpeg.exe` and `ffprobe.exe` in a `tools` folder beside the executable (or on `PATH`), `libmpv-2.dll` beside the executable or in `tools`, and an `upscale` folder containing `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `DirectML.dll`, and `model.onnx`. The `fonts` folder with Pretendard is in this repository; keep it next to the executable.

### License

Love My Shelf is free software under the [GNU General Public License v3.0](LICENSE). You may use, share, and modify it under that license. The bundled components keep their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Contact: lovemyshelf.app@outlook.com
