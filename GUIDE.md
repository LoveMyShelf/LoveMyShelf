# Love My Shelf guide

[한국어 설명서](GUIDE.ko.md) · [Back to README](README.md#english)

### What kind of program Love My Shelf is

Love My Shelf is a program that lays out a folder piled with photos, GIFs and videos like a grid and shows it at a glance. You can tell what is there without opening the files one by one. When you find a file you like, you can pick one photo or one scene of a video and cut it into a tall 9:16 picture. A picture made this way is called a **cover**. Once you make a cover, its tile shows the cover, so you recognize it right away the next time you open the folder.

The original file is never changed. A cover is saved as a separate JPG file in the folder you chose.

### Installing and the first run

1. Download [**LoveMyShelf-Windows.zip**](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Windows.zip).
2. Right-click the ZIP file → **Extract All…** → **Extract**. Be sure to extract the whole ZIP. If you run it straight from inside the ZIP, it can't find the files that must be with it and doesn't work properly.
3. In the extracted folder, double-click **LoveMyShelf.exe**.
4. A **‘Windows protected your PC’** window may pop up. That is because the program isn't signed with a paid certificate. Click **More info** → **Run anyway**.
5. Choose **한국어** or **English**. You can change it later in **Guide & Options**.
6. Read the short introduction and click **Let’s start**.
7. The first time you save a cover, it asks for the folder to save covers in. For example, make a `Pictures\Covers` folder and choose it.

Requirements: Windows 10 or 11 (64-bit). The .NET Framework 4.8 already in Windows is enough. Nothing needs to be installed separately. The FFmpeg needed for video covers is in the ZIP too.

### Files it can open

- **Photos:** JPG, JPEG, JFIF, PNG, BMP, TIF, TIFF
- **Moving pictures:** GIF, WebP (including animated WebP)
- **Videos:** MP4, M4V, MOV, MKV, WEBM, AVI, WMV, MPG, MPEG, MTS, M2TS, TS, 3GP

### Guide & Options

Click **Guide & Options** at the top right, or press **F1** on any screen, to open it. Pressing F1 opens the explanation for the screen you're looking at right away.

- Click an item you're curious about in the list on the left, and its explanation appears on the right. Shortcuts are drawn like keyboard keys, and button names like the real buttons.
- Choose the setting for that feature in the **Set it right here** box just below the explanation. All the choices are laid out, and each one says what it means.
- The moment you choose, it is saved and applied right away. There is no need to press a separate Save button.
- The one marked **Default** is the setting that was chosen from the start. If you're not sure, just leave it at the default.
- Every setting is gathered in **All settings at a glance**. Check the current value and click **Go to change it** to go straight to its explanation.

### Light screen and dark screen

In **Guide & Options → Screen, language, updates**, choose **Follow Windows** (default), **Light** or **Dark**. With Follow Windows, it changes along right away even if you change the Windows setting while the program is running. Where photos and videos are shown, the background is black in every mode so that colors look right.

### Main screen

- To add a folder, right-click it in the folder tree on the left and click **Add to Love My Shelf** (it is like ‘Pin to Quick access’ in File Explorer). You can also drag the folder onto Love My Shelf in the tree, or drag it from File Explorer onto this window. **Ctrl+O** opens the folder selection window. Folders you added show under Love My Shelf in the tree.
- Right-click a folder in the tree and click **Remove from Love My Shelf** to take it off the list. The actual folder and files stay as they are.
- With the **All / Finished / Unfinished** buttons at the bottom left, you can choose which tiles in the folder to see: everything, only those with a cover, or only those that have no cover yet.
- **Unfinished cover collection** gathers in one place only the files that have no cover yet, from the folders you added to Love My Shelf and their subfolders. Even if you add a whole drive, Windows system folders are left out. Good for making covers one after another.
- **Cover collection** gathers the covers made in every folder on one screen. If one original has several covers, all of them show.
- Click the **Sort** button at the bottom right and the choices unfold upward (it is the same as sorting in File Explorer). Choose one of **File name**, **Date modified**, **File size**, **File type** and **Cover creation order**, and which end comes first (for example A → Z or Newest first), and the folder is read again and sorted in that order. **Shuffle** at the very bottom mixes them at random. A shuffled order is temporary, so choosing a sort again brings the original order back.
- Each time you click **Cover size**, the tile size changes through 4 steps.
- The top row is the same as File Explorer: **←** back (**Backspace**, **Alt+←**, the mouse's back button), **→** forward (**Alt+→**), **↑** the folder above (**Alt+↑**) and **⟳** refresh, and the **address bar** shows where you are now. In the address bar, click a folder name to go to that folder, or click the **›** after a name to see the list of folders inside it. Click the empty part (or press **Ctrl+L**) to type or paste a path; folder names are suggested as you type.
- Click the button at the very top left (or press **Ctrl+T**) and the **folder tree** opens on the left, like File Explorer. Love My Shelf is at the top (the two collections and the folders you added), and below it are your pinned folders, This PC and the network. Click a folder to open it, and click **▸** to unfold the folders inside. Drag files onto a folder in the tree and they move to that folder. To change the order of the folders you added to Love My Shelf, drag one onto another in the tree.
- **Selecting many:** hold the right mouse button and drag over the tiles, and every tile the rectangle touches is selected (drag while holding Ctrl to add, Esc cancels, and the left button works too from an empty spot).
- **Ctrl+C / Ctrl+X / Ctrl+V** copy, cut and paste files. It works the same way as File Explorer. When files move, their covers go along, and Ctrl+Z undoes it.
- Right-click a file or folder and Love My Shelf's menu comes first, then, below a dividing line, File Explorer's full menu. Right-click an empty spot and the folder's menu appears (View, Sort by, Refresh, Paste, Undo move, and below them New, Open in Terminal, Properties and so on).
- The subfolders in a folder gather separately in a folders area above the photos (you can change it to below in Options), set apart by a horizontal line. Click **View** on that line to choose a view as in File Explorer: icons, list, details, tiles or content. Double-click a folder to go into it, and come back with **←** or **Backspace**. Drag files onto a folder and they move to that folder. In a place with only folders and no photos or videos, the folders unfold by themselves even if you folded them.
- After adding or moving files, press **F5** to refresh the screen.
- Click the **magnifier** at the top right (or press **Ctrl+F**) and the search box opens. Type something and only the files whose names contain it stay. Press **Esc** to clear the search and close the box.
- Point the mouse at a tile and, after a moment, the whole file name appears right below it (above it on the bottom row). It doesn't cover the tile you're pointing at, and a long name wraps onto more lines so it shows to the end. To always show file names under the tiles, choose One line, Two lines or Whole name in **Guide & Options → Browsing files**.

### Using it like File Explorer

| Key | What it does |
| --- | --- |
| **←** **→** **↑** **↓** | Move between tiles (nothing opens) |
| **Enter** | Open the selected tile large |
| **Home** / **End** | First tile / last tile |
| **Ctrl+click** / **Shift+click** | Add or remove one tile at a time / select everything from the last selected tile to this one |
| **Ctrl+A** | Select all |
| **Ctrl+N** | Open one more window (for dragging files between two folders) |
| **Ctrl+Z** | Undo the last move |
| **Esc** | Clear selection |
| **F2** | Rename |
| **Delete** | Move to the Recycle Bin (the original in a folder, the cover file in Cover collection) |

- Select several tiles, then right-click one of them to **Copy**, **Share**, **Hide** or **Move to the Recycle Bin** all at once.
- Right-click a tile for **Rename**, **Open with default app**, **Open file location**, **Open cover file location** and **Details** (size, resolution, length, date, number of covers).
- **Moving files to another folder:** press **Ctrl+N** or click **New window** and one more window opens next to this one. In the new window, open the folder you want to move the files into, then drag the tiles you selected in the first window onto the new window. A window you can drop on lights up at its edge and tells you how many will move. The covers made from a file go along with it, so their link to the original stays as it is. Hold **Ctrl** as you let go to copy instead of move. If the folder already has a file with the same name, it asks what to do: **Replace** (the existing file goes to the Recycle Bin), **Skip** or **Keep both** (a number is added after the name of the file being moved). Nothing is ever overwritten without asking. If you moved something by mistake, **Ctrl+Z** undoes it.
- **Rename** leaves the extension as it is and also renames the JPG covers, upscaled covers and GIF covers made from that file. A name that already exists can't be used.
- Tile positions don't change by themselves. Even when you make, hide or recycle a cover, the other tiles stay right where they are (a removed tile stays as an empty slot). Press **F5**, choose a **Sort** again, or reopen the screen to tidy up.
- Click **Open cover folder** next to **Sort** at the bottom right and the folder with your covers opens in File Explorer. If you chose to save covers in the same folder as the original, the folder you're looking at opens.
- The numbers at the bottom are the number of files on screen now and how many of them have a cover.

### File queue

1. Drag a file or folder tile to another folder. If you add several jobs, they run in order. Click the **⇄** button in the bottom bar of the folder window to open the queue.
2. Click **Show the files** to see finished files (✓), the file being sent (▶ and its progress), and waiting files. Click **To the file being sent** to return to the current row. Double-click a row to open the folder containing that file.
3. Click **Pause** to stop for a while, **Go on** to continue, or **Cancel** to stop the job. Reorder waiting jobs with **↑**, **↓** or **To the front**. After closing and reopening the program, click **Go on** on an unfinished job.

To read each copy back and compare it with the original, open **Guide & Options → Browsing files → Check that each copy is the same as the original?** and choose **Check every copy**.

### Making a cover from a photo

1. Double-click a photo to open it large and click **Make cover**.
2. A tall frame appears over the photo. The part you see inside the frame becomes the cover just as it is.
   - **Drag** the photo to change its position.
   - Roll the **mouse wheel** to zoom in and out.
   - **Rotation**: drag the slider, or turn it 0.1° or 90° at a time with the buttons.
   - **Brightness**: drag the slider to make it brighter or darker.
   - Click **Reset** and both rotation and brightness go back to how they started.
   - If you choose <b>Stick (on)</b> in **Guide & Options → Making a cover**, an edge of the photo sticks like a magnet when it comes close to the frame. Keep dragging and it comes loose again.
3. Click **Save cover**. Once saved, the cover you made opens large right away. The cover is cut straight from the original and saved as a JPG at the best quality. Empty black bars at the top and bottom are cut off automatically. The tile in the list changes to the new cover too.
4. To leave without saving, press **Esc**.

Example: to make a cover where a face fills the screen, zoom in until the top of the frame touches the top of the head, then move the photo so that the face is in the middle.

### Making a cover from a video, GIF or WebP

It works as soon as you extract the ZIP. The FFmpeg tools it needs are in the `tools` folder.

1. Open a video, play it, and pause near the scene you want.
2. Click **Make cover** and the scenes before and after the paused spot appear in a list.
3. The **Scene size** slider changes the size of the pictures in the list. Click **All scenes** to see the scenes of the whole video.
4. Click the scene you want and the Make cover screen opens with that scene.
5. In the Make cover screen too, **Previous scene** and **Next scene** (**D** and **F**) let you compare with the scenes right next to it. The keys are the same as in the large view: **←** and **→** skip by the seconds you set (1 second with Shift), **Q**, **W** and **E** are reset, darker and brighter, and **R** switches the brightness style. Fit it in the frame just as with a photo and click **Save cover**.

Tip: pressing **D** or **F** during playback pauses and moves one scene back or forward. It is handy to find the exact scene and then click **Make cover**.

### Viewing, editing and making one more cover

- **Double-click** a tile with a cover and the side you set in Guide & Options (the cover or the original) opens large. One click selects the tile. To open with a single click, change it in **Guide & Options → Large view**.
- **Right-click** a tile to choose between **View cover large** and **View original large**.
- Inside the large view, **View cover** and **View original** switch between the two.
- **Edit cover** opens the Make cover screen with the framing you saved. Good when you want to change only a little. For a video, the scene you saved opens.
- **Add cover** in the large view (or **Make one more cover** when you right-click a tile) makes one more cover from the same original. Use it when you want another scene or another framing. The covers you made before stay as they are. In the folder view, that many tiles show side by side. A video's new cover goes in the place that fits the time order of the scenes in the video. These tiles are all made from the same original, so Delete (the original goes to the Recycle Bin and all of that original's tiles disappear), Rename, moving to another folder and Open file location apply once to the one original, whichever tile you use. **Cover collection** also shows one tile for each cover.
- **A cover from a picture made elsewhere**: right-click a tile and choose **Use a picture made elsewhere as a cover…**, then pick a picture file (JPG, PNG, WebP or BMP). The same Make cover screen opens with that picture, so you can cut, turn and brighten it in the same way. A poster or a picture you edited in another program can become the cover of a video or photo like this. Love My Shelf keeps its own copy of the picture you picked, so you can move or delete your picture file afterwards and **Edit cover** still works. When you move the cover to the Recycle Bin, the finished cover file goes to the Recycle Bin and the kept copy is removed with it. A cover made this way has no scene in the video, so **Make GIF cover** doesn't appear for it.
- **Changing the order of covers**: when one original has two or more covers, right-click a cover and open **Cover order**: **Move forward**, **Move back**, **Move to the very front (the first cover)** and **Back to the automatic order**. Once you move a cover yourself, that original keeps your order, and a cover you make later goes to the end.
- Right-click a tile and click **Open with default app** to open it in the program set as the default in Windows.
- The other way round, right-click a photo or video in File Explorer and click **Open with Love My Shelf**: the folder that holds the file opens with that file right in the large view (on Windows 11, click **Show more options** first, or press **Shift+F10**). It is on from the start, and you can turn it off in **Guide & Options → Screen, language, updates**.

### JPG covers and GIF covers

A still cover is called a **JPG cover**, and a moving cover is called a **GIF cover**. It is called a GIF cover even when saved as WebP or MP4.

### Making GIF covers

A cover made from a video, GIF or WebP can also be made into a short moving cover that uses the same frame, rotation and brightness. Start from **Make GIF cover** in the Make cover screen (next to Save cover), **Make GIF cover** in the large view (next to Upscale), or a tile's right-click menu.

You can also set the part in advance while watching the video. In the large view, press **[** where it starts and **]** where it ends (the same buttons are under **GIF cover part** in the right-click controls window): only that part plays again and again, and when you then make a cover as usual from a scene inside the part, the window below opens on that part instead of 1 second before and after the cover (when the cover's scene is more than 2 seconds away from the part, it opens as usual). **\\** clears the part.

1. At the bottom of the window you see the **scene strip**, with the cover's scene (the small card over the pictures) in the middle. Over the pictures runs a thin bar like a slider, and its colored part is what goes into the GIF. How much the strip shows at first is set in **Guide & Options → Making GIF covers** (default 4 seconds).
2. Drag the two round **handles** at the ends of the colored part to the start and the end. Click <b>‹</b> / <b>›</b> at **Start** and **End** under the strip, or press **←** / **→**, to move one scene at a time (**←** / **→** move the handle you last held). One scene is the **scene step** in **Guide & Options → Scenes from videos** (default 0.2 seconds). Handles also move one scene at a time when you drag them.
3. Drag the pictures in the strip sideways, or roll the mouse wheel over the strip, to see other parts of the video. Each notch of the wheel moves a quarter of the strip: down goes later, up goes earlier. The handles stay where they are. Drag a handle to the end of the strip and the strip moves along. Click **Back to the cover** to return to the cover's scene. **Double-click** outside the part and the part grows to there (to the left of the part the start comes there, to the right of it the end). The hints for dragging, the wheel and the double-click always show under the strip (over it in a narrow window), and the one you can use where the mouse is shows darker. There is no limit on the length.
4. Choose the **Speed** (0.2× to 4×. **−** / **+** or **X** / **C** change it by 0.1× at a time, and holding one keeps it changing. **Z** is the original speed, and **V** goes straight to 2× and back to 1× when pressed again) and the **Play style** (**Loop**, **Back and forth**, which goes to the end and comes back in reverse, or **Backward**).
5. When you change the part, the **preview** shows at the top right away. The preview is scenes taken from the original at wide intervals (0.05 seconds apart by default), so it looks a little choppier and less sharp than the real file. The card on the left of the preview says what you are looking at now. The round dot moving under the strip's pictures is where it is playing now. Click the preview or press **Space** to pause and play again. The length, the number of pictures, the size (width × height) and the file size show too (a rough value like ‘about 3 MB’). To see how it will be saved first, click **See how the real file looks** on the card. Then the **real file** is made and plays as it is in the preview's place, with its progress and its exact size. For 3 seconds long, 30 pictures a second and 1080 pixels tall, MP4 takes about 1 second, GIF about 2 to 3 seconds, and WebP about 5 seconds.
6. On the right of the preview you can choose the speed and the play style, the smoothness (pictures per second), the height, the picture quality (High, Normal, Low) and the file type (GIF, WebP, MP4) (in a narrow window they move down under the strip, and the last four are under **More choices**). The values selected at first when the window opens (file type, picture quality, smoothness, height, and how much before and after the cover) are set in **Guide & Options → Making GIF covers**. Click **Make** (in the Make cover screen, **Save JPG cover and make GIF cover**) and, if the real file is already made, that file is saved right away; if not yet, it is made in the background and saved.
7. How the preview works is yours to choose. Click **Change how the preview works** on the card, or in **Guide & Options → Making GIF covers** choose ① whether the real file is made only when **Make** is clicked (the default) or by itself in the background once the part is set (then, when you let go of the handle and wait a moment, the preview changes to the real file) ② when it is made by itself, how long to wait after you let go of the handle (0.7 seconds by default) ③ the spacing of the preview's scenes (0.05 seconds by default) ④ how carefully WebP is compressed (Carefully about 5 seconds · 1.9 MB / Quickly about 3 seconds · 2.0 MB / Very quickly about 2 seconds · 2.6 MB, for the same example as above).
8. Choosing a height bigger than the original does not stretch it. For example, if the cover is 1080 pixels tall in the original, it is made 1080 pixels tall even if you choose 1280, and then a line under the height says so.

If the window before it is in full screen, the Make GIF cover window opens in full screen too. **F11** or the full screen button at the top right changes it.

Both the JPG cover and the GIF cover are saved. A tile shows the JPG cover first, and the GIF cover moves when you point at it (you can change this in **Guide & Options → Making GIF covers**). In the large view it plays automatically, and **View JPG cover** and **View GIF cover** switch between the two.

#### GIF, WebP, MP4: what's the difference?

None of the three has sound. The sizes below are for a cover 3 seconds long, at 15 pictures a second and 960 pixels tall. How each one looks where you post it was checked in November 2026, so it may differ if the apps change.

- **GIF**: the most widely used format for moving pictures, so it moves almost everywhere. But each picture can use only up to 256 colors, so where colors change smoothly, as on faces or under stage lights, tiny dot patterns or step-like lines can appear. Its files are the biggest of the three (usually 7 to 13 MB).
  - Discord: it moves (free users can upload up to 20 MB per file). X (Twitter): up to 15 MB on a computer and 5 MB on a phone.
- **WebP**: a fairly new format made by Google. It moves like a GIF but holds colors much more smoothly, and its size is about a tenth of a GIF (about 1 MB). But in older programs and some apps, only the first scene shows as a still picture, or it doesn't open at all.
  - It moves like a GIF on Discord too. It is the best for keeping and viewing inside Love My Shelf.
- **MP4**: a video format. Of the three, its size is the smallest (about 0.8 MB) and its picture quality is the best. But it is treated as a video, not a picture, so depending on where you post it, it may get a play button or play once and stop.

If you're not sure, choose WebP (it is selected at first); if you'll post it where WebP doesn't move, GIF; if you're fine with it going up as a video, MP4.

- **Sound in an MP4**: Choose MP4 as the file type and **Put the sound in** appears under it. Check it, and the video's sound for the same part goes into the file. Change the speed and the sound changes to the same speed. It goes in only when the play style is **Loop**, not with back and forth or backward. In the folder window, point at a tile and the cover moves with its sound. To turn the sound off, choose **No sound** under **Should a tile play its sound when the mouse is on it?** in Guide & Options. In the large view the cover moves without sound. The sound plays when you open or share the file somewhere else.

### Large view and playback

| Key | What it does |
| --- | --- |
| **K** / **L** | Previous file / next file |
| **Space** | Play / pause |
| **←** / **→** | Skip back / forward (default 5 seconds; you can change it in Guide & Options) |
| **D** / **F** | Pause and go one scene back / forward |
| **Q** / **W** / **E** | Back to the original brightness / darker / brighter (default 3 at a time; you can change it in **Guide & Options → Scenes from videos**). Moving to another file returns it to the original brightness. |
| **R** | Switch the brightness style (each press moves to the next): Soft (the usual one, brightens faces gently and brightly) / See in the dark (lifts only the dark parts a lot, so things are easy to tell apart even in a very dark video) / Milky (as if laying a white veil over the whole screen, all white at +100) |
| **Z** / **X** / **C** | Original speed / 0.1× slower / 0.1× faster |
| **V** | Straight to 2× speed (press again to return to 1×) |
| **I** / **O** / **P** | Rotation back to normal / turn left / turn right (default 10° at a time; you can choose from 1° to 45° in **Guide & Options → Scenes from videos**. Hold the key and it keeps turning, resting a moment at every 90°. **I** puts the angle, size and place all back) |
| **Mouse wheel** | Zoom in / out (up to 8×. A turned picture can zoom out until all of it fits. If part of it is past the edge of the screen, drag to move it into view) |
| **↑** / **↓** | Volume up / down by 5 (the next video starts at that volume too) |
| **M** | Sound off / on |
| **N** | Clear voice on / off (it stays on for the next video too; choose Gentle, Normal or Strong in the controls window that opens when you right-click the screen) |
| **/** | Capture this scene (at first it is saved as a PNG in the `Pictures\Love My Shelf 캡처` folder. The folder, whether to save the video's original frame or just what the screen shows now, and PNG or JPG can be changed in **Guide & Options → Scenes from videos**) |
| **[** / **]** / **\\** | Start / end / clear the GIF cover part (once both the start and the end are set, the playback bar shows the part as a pale band and only that part plays again and again. Make a cover from a scene inside the part in this state and the **Make GIF cover** window opens on that part. A part can be up to 60 seconds, and it is cleared when you make a GIF cover from it or move to another file) |
| **Tab** | Show / hide the file information (the file name, which drive and folder it is in, format, size, picture quality, fps and so on) |
| **Delete** | Move the original file you are looking at to the Recycle Bin and go on to the next file |
| **Right-click** | Open the controls window (every control is listed with its key, and you can click it to use it right away) |
| **F11** | Full screen on / off |
| **Esc** | Close the large view |

- The playback bar at the bottom has the playback position and the **Volume**. **X** / **C** change the speed by 0.1× (0.2× to 4×), and **Z** returns to 1×.
- **Right-click** the screen and the controls window opens. Every control is listed with its key, such as moving by scene (D, F), brightness (Q, W, E, R) and rotation (I, O, P), so you can look here when you can't remember a key. Clicking a button with the mouse does the same thing, and holding it down keeps it going. Capturing a scene, the subtitle explorer, Clear voice and Open file location are here too.
- Press **O** or **P** and a video stops on that scene and becomes a small picture that never touches the edges of the screen at any angle. About 0.5 seconds after you let go, it plays on from the same scene at that angle. Set the angle first, then adjust the size with the wheel.
- Rotation and zoom stay only while you look at that file. When you go on to the next file they return to normal. The seek-bar thumbnail looks just like the screen, and if you click **Make cover** like this, the scene list and the cover (GIF covers too) continue at the same angle, size and place.
- Press **Delete** and the file goes to the Recycle Bin right away, with no confirmation window. If you sent it by mistake, you can restore it from the Windows Recycle Bin. A large view opened from **Cover collection** doesn't delete originals.
- When you change the playback speed, brightness or volume with keys, a value like ‘Playback speed 1.1×’ shows briefly at the top left of the screen, as in PotPlayer.
- In **Guide & Options → Large view** you can choose which speaker each monitor's sound comes from. Move a video window to another monitor and, about a second later, its sound follows to that monitor's speaker. The volume is remembered separately for each speaker. If the chosen speaker is unplugged, the sound comes from the Windows default speaker.
- Point at the playback bar and the scene at that spot appears as a small picture with its time. Click to go to that spot.
- In windowed mode the playback bar is always below the video, as in PotPlayer, so it doesn't cover the video. In full screen, move the mouse to the very bottom and the playback bar and buttons appear over the video; move it to the very top and the title bar appears.
- **Double-click** the video to pause or play again.
- The large view fills the window from top to bottom. A wide photo may look a little cut off at the sides. The black bars at the top and bottom of a saved cover don't show.
- In full screen too, the minimize and maximize buttons at the top right can be clicked right away. Minimize it and bring it back, and it returns in full screen as it was; maximize leaves full screen and becomes a maximized window. This is the same in every Love My Shelf window.
- When you watch only a video, try minimizing the folder window. About 5 seconds later it lets go of the pictures it had read to show in the tiles, so Love My Shelf uses less memory. While watching a video, right-click the screen and click **Minimize the folder window**. Bring the folder window back and only the pictures of the tiles on screen are read again.

### Changing the large view’s shortcuts

1. Press **F1** to open Guide & Options and click **Advanced** at the bottom of the list on the left.
2. Click the key to the right of the action you want to change, then press the single key you want on the keyboard. The change takes effect even in large view windows that are already open.
3. To restore all the original keys, click **Put all keys back** at the bottom. The guide and button tooltips show the original keys.

### Subtitles and sync

If there is a subtitle file (SMI, SRT, ASS, VTT) with the same name as the video, it shows automatically. If one file has both Korean and English, Korean shows first. In full screen, while the playback bar is showing, the subtitles move up above the playback bar so they aren't covered.

Subtitles show in the same font as Love My Shelf. A font or size written inside an SMI or SRT file is not followed (colors are kept), and ASS subtitles show in the fonts their maker chose. Change the subtitle size by 10% at a time (50 to 300%) with **Subtitle size** in the controls window that opens when you right-click the screen, or choose it in **Guide & Options → Large view**. The size you set is used for the next video too.

1. Right-click the video screen and click **Open the subtitle explorer**.
2. Pause at the moment a line of speech starts.
3. Click the subtitle line for that speech once. One click only selects the line, and the video doesn't move.
4. Click **Sync to the selected line** and the sync of the whole subtitle is set.
5. If it fits, click **Save** to write it over the subtitle file.

- Double-click a subtitle line and the video goes to that spot.
- If only the later part is off, choose **Move only this line and after** from the ▾ next to the sync button.
- Saving changes only the times that moved and the sentences you fixed. The subtitle's first state is kept separately in Love My Shelf's records folder.
- Subtitles inside a video file also show as usual, but only separate subtitle files can be listed and synced.

### Built-in video player

The ZIP includes `libmpv-2.dll`, the playback engine of the free player mpv. It plays smoothly even high-resolution videos and VP9, AV1 and HEVC videos that stutter in the Windows player. If you delete this file, videos play with the Windows player.

### Font

Love My Shelf's text is shown in Pretendard, a free font made with both Korean and Latin letters in mind. The font files are in the `fonts` folder in the same folder as `LoveMyShelf.exe`, so they aren't installed on your PC. Without this folder, text is shown in Segoe UI and Malgun Gothic.

### Upscale (making a small cover sharper with AI)

If a saved cover is less than 1920 pixels tall, an **Upscale** button appears in the large view.

1. Click **Upscale** and an AI model (Swin2SR) enlarges the cover 4 times and makes it sharper. It runs by itself in the background, so you can keep using the program meanwhile.
2. The button shows the progress, like <b>Upscaling 40%</b>. On a laptop CPU, one small cover takes from 30 seconds to a few minutes.
3. When it's done, a notice appears at the bottom right. Click the notice, or click that cover's tile once, and the compare window opens.
4. The original cover and the upscaled cover fill the window from top to bottom and touch in the middle (wide covers are stacked one above the other). If you are using full screen, the compare window opens in full screen too, with nothing above or below the pictures; the title bar appears when you move the mouse to the top. Zoom with the wheel and drag to move, and the two pictures move together. Double-click to go back to how it was.
5. Click **Use this cover** beside the one you like. Click **Decide later** to put off the decision.

Both JPG files stay where the cover is saved. The upscaled file's name ends in `-upscaled.jpg`. Click **Compare upscale** to open the compare window again and change your choice any time. If you make the cover again, the link to the upscaled file is broken, but the file isn't deleted.

### Tidying up

- **When one original has several covers**: a number like **2/5** shows at the bottom right of the tile. It means the second of 5 covers. Covers of the same original always stay together even when you sort or shuffle, with number 1 on the left. Covers from pictures made elsewhere come first, in the order you added them, and then a video's covers are placed in the time order of their scenes in the video, regardless of the order you made them. An original whose covers you arranged yourself keeps your order. Point at a cover and all the covers made from the same original get a colored outline. In the large view, when there is empty space on the right of the screen, the other covers of the same original appear small; click one to switch to it. In full screen they hide when the mouse stays still and come back when it moves.
- **Hide this cover**: leaves the file as it is and only stops showing it in Love My Shelf. It leaves **Cover collection**, and the tile in the folder view shows the original (the tile of a cover you made as one more disappears from the folder view). Click **Show this cover again** to bring it back. If you hid another cover of the same original, **Show hidden covers again** appears when you right-click the tile in the folder view.
- **Move this cover file to the Recycle Bin**: sends the cover JPG (together with the upscaled file and the GIF cover, if there are any) to the Windows Recycle Bin and removes it from the list. The original stays as it is. If there is a GIF cover too, the item's name changes to **Move both the JPG and GIF covers to the Recycle Bin**, and one more item appears: **Move only the GIF cover to the Recycle Bin**. If you send only the GIF cover, the JPG cover stays, and restoring the GIF file from the Recycle Bin connects it again. Hide and show also apply to both covers, so their names change to **Hide both the JPG and GIF covers** and **Show both the JPG and GIF covers again**.
- **If you deleted a cover by mistake**: when you send a cover to the Recycle Bin, a notice shows at the bottom right for 3 seconds. Click the notice within that time and the cover comes right back. Keep the mouse over the notice and it waits without disappearing.
- **Move the original file to the Recycle Bin**: sends the original photo or video itself to the Windows Recycle Bin. You can restore it from the Recycle Bin.
- **If you moved an original**: an original that you renamed in File Explorer or moved within the same drive is usually found again by itself. When it can't be found, the tile shows the small saved preview. Open it large, click **Find the original again** and choose the file in its new place.
- **If you unplugged an external drive and connected it again**: connect the drive again and its covers and folders come back by themselves. It's fine even if the drive letter changes. While the drive is unplugged, a message says the drive isn't connected, and the covers and originals are still there.

### Tags and finding people by face

- **Putting on a tag**: Right-click a tile and click **Tags…**. Type a name and press Enter, and the tag is on. Tags you used before show below, so one click is enough. You can select several tiles and tag them at once. A tag goes on the original, not on one cover, so an original with several covers needs it only once.
- **Where tags are kept**: Only in Love My Shelf's records. The original files and their names are never changed. When you move or rename a file inside Love My Shelf, its tags go with it. A file renamed in File Explorer, or moved within the same drive, is mostly found again by itself and its tags follow. If you did that while the program was open, press F5. A file moved to another drive loses its tags unless it has a cover.
- **Seeing a tag's files together**: Click **Tags** at the bottom center to see the list of tags and how many files each one is on. Click a tag, and the files with that tag are gathered from every folder. Right-click a tile on this screen and **Take the tag “name” off** appears. At the bottom of the same list you can rename or delete the tag you are looking at.
- **Search**: The search box finds file names and tags together. Put # in front, like `#name`, and it looks at tags only.
- **Finding people by face**: Click **Find people by face…** at the bottom of the **Tags** list. Click **Find faces**, and Love My Shelf looks for faces on your covers and shows faces that look alike together. Type a name for a group and click **Name them and tag**, and that name becomes a tag on the originals of those covers. The program doesn't know who anyone is; it only gathers faces that look alike. You give the names.
- **When the groups are wrong**: If one person is split into several groups, move the slider left. If different people are in one group, move it right. It starts at 0.36. Click faces to choose them, then click **Take the chosen faces out** or **The chosen faces are not name**, and only those faces leave. Give two groups the same name and they become one person.
- **After making new covers**: Open the window again and click **Find faces on new covers**. Faces that look like a person you named are shown apart under that person, and the tag goes on only when you click **Yes, they are name**. Tags are never put on without asking.
- **The pictures never leave**: Finding faces runs on this computer only. Nothing is sent over the internet. The numbers that describe a face are kept in Love My Shelf's record folder, and **Delete all face information…** removes all of them. Tags you already gave stay.

### Copy, share, drag and drop

Right-click a tile and click **Copy** or <b>Share…</b>, or drag the tile onto a chat window or a folder.

- **Copy** puts the files on the clipboard. Press **Ctrl+V** in a chat window or a folder to paste.
- <b>Share…</b> opens the Windows share window. Choose the app to send with there.
- What is sent is set in **Guide & Options → Sharing**. You can choose **Cover and original** (default), **Cover only** or **Original only**. Right-click a tile to choose differently just this once. A GIF cover made from a cover is sent along with the cover.
- If an original video is bigger than the size set in Guide & Options (default 20 MB), copying or sharing asks whether to **Send it as it is** or **Send only 10 seconds around the cover scene**. Dragging always sends the file as it is.

Love My Shelf never uploads anything to the internet itself. It only hands the files to Windows, and where they go is decided in the app you chose.

### What you can set in Guide & Options

- **Browsing files**: whether the folders go above or below the photos, the folder view, the sort order of the tiles (File name, Date modified, File size, File type, Cover creation order) and which end comes first, and whether to show file names under the tiles (No name, One line, Two lines, Whole name). The sort is the same setting as the **Sort** button at the bottom right.
- **Making a cover**: whether covers show their scene time (where the scene is in the video, like 12:34), whether the frame sticks to the photo's edges, how much one wheel step zooms (3, 5, 7, 10 or 15%), and whether the top line of the frame stays fixed when you zoom with the wheel.
- **Saving covers**: where covers are saved (① Gather them in one cover folder ② Save it in the same folder as the original ③ Make a ‘Love My Shelf 표지’ folder inside the original's folder and save it there) and the cover folder. Even with ② or ③, if the place can't be saved to, the cover is saved in the cover folder instead and you get a notice. Even if you change this setting, covers already made are not moved.
- **Scenes from videos**: how far the ← and → keys skip (3, 5, 10 or 30 s), how much the E and W keys change the brightness, the scene step, the folder for captured pictures, what a captured picture looks like (the video's original frame / just as it looks on screen now), and the kind of file for captured pictures (PNG / JPG).
- **Making GIF covers**: the starting file format, picture quality, pictures per second, height, how much before and after the cover at first, how much the scene strip shows at first, when the real file is made, how long to wait after you let go of the handle, the spacing of the preview's scenes, how carefully WebP is compressed, and whether GIF covers move in the tiles.
- **Large view**: whether a double-click or one click opens it, and whether the cover or the original shows first when you open a photo tile or a video, GIF or WebP tile, the subtitle size (70 to 200%), and the speaker for each monitor.
- **Sharing**: what Copy, Share and dragging send, and above which size (10, 20, 50 or 100 MB) it asks before sending a video.
- **Screen, language, updates**: light or dark screen, 한국어 or English, new version notices.

Most settings that choose a number have a **Type your own** card under their choices. Enter the number you want and click **Apply** or press **Enter**. **−** / **+** change it one step at a time; hold one and it keeps changing, and it is saved when you let go. The range you can enter is written under the box, and a number outside it is set to the nearest value and you are told so. While a value you typed is in use, the card shows something like ‘✓ Type your own · now 0.07 s’.

### Where are my files?

- **Covers:** in the place you set in Guide & Options, as ordinary JPG files (GIF covers made as GIF, WebP or MP4 are there too).
- **Program records** (folder list, cover framing, settings): `%LOCALAPPDATA%\LoveMyShelf\Folders`. Paste it into File Explorer's address bar to open it.
- **Originals:** right where they were. Love My Shelf only reads the originals.
- The internet is used only to check for a new version and, after you click to update, to download the new files. No photos or personal information are sent. You can turn it off in **Guide & Options → Screen, language, updates**.

When a new version is out, click the notice at the bottom right, then click once more on the notice that says it is ready, and Love My Shelf starts again as the new version. Your covers and records stay as they are. Only when coming from version 1.0.3 or earlier, one last time, close the program and extract the new ZIP over the old folder.

### FFmpeg for video covers

`ffmpeg.exe` and `ffprobe.exe` are used when choosing scenes from a video. The two files and the DLL files they need are already in the `tools` folder in the same folder as `LoveMyShelf.exe`, so there is nothing to prepare. Leave the `tools` folder right where it is.

If you delete the `tools` folder, Love My Shelf looks for an FFmpeg registered on `PATH`. To use a different FFmpeg, replace `ffmpeg.exe` and `ffprobe.exe` in `tools`. For a "shared" build, the DLL files must be replaced too.

### If something goes wrong

- **A ‘Windows protected your PC’ window pops up** → Click **More info → Run anyway**. The window appears because the program isn't signed.
- **‘Love My Shelf is already running’ appears** → Only one can run at a time. Click the Love My Shelf that is already running on the taskbar.
- **‘.exe was not found’ appears when making a video cover** → The `tools` folder is missing or some files are gone. Extract the whole ZIP again.
- **The Upscale button doesn't show** → The cover is already 1920 pixels tall or more, or there is no `upscale` folder in the same folder as `LoveMyShelf.exe`.
- **A video doesn't play or stutters** → Check that `libmpv-2.dll` is in the same folder as `LoveMyShelf.exe`. You can also right-click the tile and click **Open with default app** to watch it.
- **‘The original was moved or renamed’ appears** → The original was moved or renamed and couldn't be found. Click **Find the original again** and choose the file in its new place.

### Building from source

On Windows, open the source folder in PowerShell and run the following.

```powershell
.\build.ps1
.\LoveMyShelf.exe
```

It builds with the C# compiler and WPF of the .NET Framework 4.x that is in Windows. Visual Studio and NuGet are not needed. To use the optional features, put `ffmpeg.exe` and `ffprobe.exe` in a `tools` folder in the same folder as the executable (or on `PATH`), `libmpv-2.dll` in the same folder as the executable or in the `tools` folder, and `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `DirectML.dll` and `model.onnx` in an `upscale` folder. The `fonts` folder with Pretendard is included in this repository, so leave it in the same folder as the executable.

### License

Love My Shelf is free software under the [GNU General Public License v3.0 (GPL-3.0)](LICENSE). Under this license, anyone may use, share and modify it. The bundled components follow their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Contact: lovemyshelf.app@outlook.com
