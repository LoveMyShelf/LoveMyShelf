# LoveMyShelf

<img src="docs/images/icon.png" width="64" align="right" alt="">

영상, 사진, 움짤이 잔뜩 쌓인 폴더를 한눈에 보는 Windows 프로그램이에요. 파일 탐색기처럼 쓰면서, 파일마다 **내가 고른 장면을 표지로** 붙일 수 있어요.

세 회사의 최상위 AI(Anthropic Claude Fable, OpenAI GPT Astra, Google Gemini Carbon)가 토큰을 아끼지 않고 서로 토론하게 해서 만들었고, 직접 쓰면서 불편한 점이 보이면 바로바로 고치고 있어요. 쓰시는 분은 시간이나 토큰을 들여 시행착오를 겪을 필요 없이 바로 편하게 쓰시면 돼요.

### [⬇ LoveMyShelf 다운로드 (Windows, 무료)](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Windows.zip)

[English is below](#english) · [설명서](GUIDE.ko.md) · [바뀐 점](CHANGELOG.md)

**내 파일은 이렇게 지켜요**

- 표지를 만들어도 원본 사진과 영상은 그대로예요. 표지는 항상 따로 저장돼요.
- 지우면 휴지통으로만 가요. 휴지통을 쓸 수 없는 곳(예: 네트워크 폴더)이면 아예 지우지 않아요.
- 다른 드라이브로 옮길 때는 복사가 끝까지 잘 됐는지 확인한 다음에 원래 자리의 파일을 지워요.
- 파일을 옮기고 지우는 부분을 고칠 때마다 가짜 파일로 자동 안전 검사 41개를 돌려요. 화면을 바꿀 때도 150%·225% 화면 배율과 반쪽 창에서 글자나 버튼이 잘리지 않는지 자동으로 확인해요.

다운로드 용량은 약 220MB예요. 영상 재생기랑 AI 기능(표지를 4배 크고 선명하게 키우기, 얼굴로 사람 찾기)이 다 들어 있어서 좀 커요. AI 기능은 내 PC 안에서만 돌아가고, 파일이 인터넷으로 올라가진 않아요.

광고나 가입, 유료 기능 없는 무료 프로그램이고, 소스 코드도 GPL-3.0으로 전부 공개돼 있어요. 사용 기록은 모으지 않고, 인터넷은 업데이트 확인할 때만 쓰는데 설명서 겸 옵션에서 끌 수 있어요.

## 이럴 땐 다른 프로그램이 나아요

- 카메라 RAW 파일을 봐야 할 때: 아직 못 열어요.
- 레이어나 세밀한 붓질처럼 본격 편집을 하고 싶을 때: 간단한 밝기·색 보정과 필요 없는 것 지우기는 되지만, 본격 편집은 다른 프로그램이 나아요.
- 휴대폰 사진을 클라우드에 자동으로 모으고 싶을 때: LoveMyShelf는 내 컴퓨터 폴더만 봐요.
- Mac이나 리눅스를 쓸 때: Windows 전용이에요.

## 받아서 켜는 법

1. 위의 **다운로드**를 누르면 `LoveMyShelf-Windows.zip` 파일이 받아져요.
2. 받은 ZIP 파일을 오른쪽 클릭하고 **압축 풀기**를 눌러 전부 풀어요. 일부만 꺼내면 영상이 안 나와요.
3. 풀린 폴더에서 `LoveMyShelf.exe`를 더블클릭해요. 따로 설치하지 않아도 돼요.

보통 프로그램처럼 설치하고 싶다면 [설치 파일(LoveMyShelf-Setup.exe)](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Setup.exe)을 받아 더블클릭해요. 관리자 권한 없이 내 계정에만 깔려 시작 메뉴에 생겨요. 지울 때는 Windows 설정 → 앱에서 지우면 돼요. 업데이트는 ZIP과 똑같이 알림으로 돼요.

처음 켤 때 파란 **"Windows의 PC 보호"** 창이 뜰 수 있어요. 개인이 만든 프로그램이라 인증서가 없어서 그래요. **추가 정보**를 누른 다음 **실행**을 누르면 돼요.

새 버전이 나오면 오른쪽 아래에 알림이 떠요. 누르면 바뀐 내용을 볼 수 있고, 한 번 더 누르면 바로 업데이트돼요. 만들어 둔 표지랑 설정은 그대로 유지돼요.

## 왜 만들었나요

다운로드 폴더나 짤 폴더에 파일이 수천 개 쌓이면 원하는 걸 찾기 힘들어요. 썸네일은 작고 영상은 엉뚱한 첫 장면만 떠서 결국 하나씩 열어 보게 돼요.

LoveMyShelf는 파일 탐색기 사용법은 그대로 두고, 파일을 큰 썸네일로 나란히 보여 줘요. 사진이나 영상에서 마음에 드는 장면을 **표지**로 저장하면 다음부터 그 표지로 보여요. 표지 모양은 세로, 정사각형, 가로 상관없이 정할 수 있어요.

## 이렇게 써요

**영상 하나로 표지 3개 만들기.** 영상에서 원하는 장면을 골라 JPG 표지 2개와 움직이는 GIF 표지 1개를 만들어요. 목록에서 GIF 표지에 마우스를 올리면 바로 움직여요.

https://github.com/user-attachments/assets/48bfd53c-b0b8-4434-aef3-d07efc0ed110

**표지는 파일을 따라다녀요.** 영상을 다른 폴더로 끌어다 놓으면 움직이는 표지까지 같이 옮겨져요.

https://github.com/user-attachments/assets/8207c877-b828-43a1-bcfd-435b6c3d31c7

**파일 탐색기처럼 써요.** 폴더 트리, 주소창, 탐색기와 같은 폴더 보기, 마우스로 끌어서 선택, 복사와 붙여넣기를 그대로 쓸 수 있어요.

https://github.com/user-attachments/assets/9d83da44-71f5-4ef2-a506-da4fc9b51617

## 할 수 있는 것

**파일 탐색기처럼 둘러보기**

- 왼쪽 폴더 트리에서 추가한 폴더, 즐겨찾기, OneDrive, 내 PC, 네트워크를 열어요.
- 주소창, 뒤로·앞으로·위로, 탐색기와 같은 8가지 폴더 보기가 있어요.
- 마우스로 끌어서 여러 개를 선택하고, Ctrl+C·X·V나 끌어다 놓기로 복사하고 옮겨요.
- 오른쪽 클릭하면 Windows 메뉴가 그대로 나와요.
- 창 두 개를 화면 반씩 나란히 놓고 정리하기 편해요.

**한눈에 보기**

- 사진, 움직이는 GIF·WebP, 영상을 한 화면에서 큼직하게 봐요. 이름 검색, 정렬, 표지 있는 파일·없는 파일 골라 보기가 있어요.
- 칸 아래에 파일 이름을 한 줄, 두 줄, 전체로 보여 줄 수 있어요.
- 여러 폴더에 흩어진 표지를 표지 모음에서 한 번에 볼 수 있어요.

**표지 만들기**

- 테두리 안에서 휠로 확대하고 끌어서 위치를 맞춘 뒤 JPG로 저장해요. 파일 하나에 표지를 여러 개 만들 수 있어요.
- 테두리 모서리를 끌거나 버튼(칸에 꽉 맞추기·칸 두 개에 꽉 맞추기·원본 그대로), 가로:세로 숫자로 표지 모양을 정해요.
- 바둑판 칸 모양은 9:16·3:4·1:1 중에 고르고 언제든 바꿀 수 있어요. 표지는 칸 안에 잘리지 않고 다 보이고, 가로로 긴 표지는 칸 두 개를 차지해서 크게 보여요.
- 영상은 멈춘 곳의 앞뒤 장면을 펼쳐 놓고 골라요. 장면 간격은 바꿀 수 있어요(기본 0.2초).
- 움직이는 표지는 GIF, WebP, MP4로 저장해요. 구간과 속도(0.2~4배)를 정하면 실제로 저장될 파일을 미리 볼 수 있어요.
- 표지를 AI로 4배 선명하게 키울 수 있어요. 결과는 원래 표지와 나란히 비교한 뒤 골라요.

**파일 정리**

- 이름 바꾸기(F2), 휴지통으로 보내기(Delete), 옮기기와 복사를 할 수 있어요. 표지는 원본을 따라가고, 옮긴 것은 Ctrl+Z로 되돌려요.
- 파일 탐색기에서 이름을 바꾸거나 옮긴 파일도 대부분 저절로 다시 찾아요.
- 외장 하드를 뺐다가 다시 연결해서 드라이브 문자가 바뀌어도 표지가 그대로 따라와요.
- 옮기기와 복사는 대기열에 줄을 서서 하나씩 진행해요. 일시정지했다가 다시 이어 갈 수 있고, 프로그램을 껐다 켜도 하던 곳부터 계속해요. 외장 하드가 빠지면 그 작업만 멈춰요.

**모아 보고 찾기**

- 폴더를 오른쪽 클릭하면 하위 폴더의 파일까지 한 화면에 모아 봐요.
- 비슷한 사진 모음: 똑같은 파일과 거의 같은 사진을 찾아 묶어요. 두 장을 나란히 보고 남길 사진을 골라요.
- 파일에 태그를 붙여 여러 태그를 함께 골라 볼 수 있어요. 원본 파일은 건드리지 않아요.
- 얼굴로 사람 찾기: 표지에서 닮은 얼굴끼리 모아요. 이름을 붙이면 태그가 되고, 내 PC 안에서만 돌아가요.

**백업과 휴대폰**

- 잠긴 백업: 표지와 기록을 비밀번호로 잠가 구글 드라이브나 OneDrive 같은 폴더에 저장하고, 필요할 때 복원해요(Argon2id, AES-256-GCM). 구글이나 마이크로소프트도 안의 내용을 볼 수 없어요.
- 잠긴 사진 상자: 원본 사진 폴더도 비밀번호로 잠가 같이 보관할 수 있어요(7-Zip AES-256). 파일 이름까지 잠겨요.
- 휴대폰으로 보내기: 표지를 순서대로 구글 드라이브나 OneDrive 폴더에 복사해서 휴대폰에서 봐요. 원하면 원본도 같이 보내요.

**영상 보기**

- 재생바 미리보기, 한 장면씩 넘기기, 이동·속도·밝기·볼륨 단축키가 있어요.
- 보던 장면을 `/` 키로 캡처해요.
- 자막(SMI, SRT, ASS, VTT)을 보여 주고, 자막 탐색기에서 싱크를 맞춰 저장할 수 있어요.

밝은 테마와 어두운 테마, 한국어와 영어를 지원해요.

## 쓸 수 있는 컴퓨터와 파일

- Windows 10 또는 11 (64비트). .NET Framework 4.8이 필요한데, Windows 10 1903 이후에는 이미 들어 있어요.
- 사진: JPG(JPEG, JFIF), PNG, BMP, TIFF, GIF, WebP, HEIC(HEIF), AVIF
- 영상: MP4, M4V, MOV, MKV, WEBM, AVI, WMV, MPG(MPEG), MTS, M2TS, TS, 3GP
- 자막: SMI, SRT, ASS, VTT
- 아직 못 여는 것: 카메라 RAW

## 지우는 법

프로그램 폴더를 지우면 돼요. 설정과 기록까지 지우려면 파일 탐색기 주소창에 `%LOCALAPPDATA%\LoveMyShelf`를 붙여 넣고 Enter를 누른 뒤, 열린 폴더를 지워요. 만든 표지는 표지 폴더에 그대로 남아요.

## 문의

버그 제보나 질문, 추가했으면 하는 점은 **lovemyshelf.app@outlook.com**으로 보내 주세요.
GitHub 계정이 있으면 [Issues](https://github.com/LoveMyShelf/LoveMyShelf/issues)에 남겨도 돼요.

## 더 알아보기

- [설명서](GUIDE.ko.md): 모든 기능과 단축키
- [바뀐 점](CHANGELOG.md): 버전별로 달라진 내용
- [이전 버전](https://github.com/LoveMyShelf/LoveMyShelf/releases)
- [개발자용: 소스에서 빌드하기와 코드 구성](docs/BUILDING.md)
- 라이선스: [GPL-3.0](LICENSE). 함께 들어 있는 libmpv, FFmpeg, ONNX Runtime, Swin2SR, LaMa, Pretendard는 각자의 라이선스를 따라요. 자세한 내용은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있어요.

---

<a id="english"></a>

# LoveMyShelf (English)

A Windows program for seeing folders piled with videos, photos and memes at a glance. Use it like File Explorer, and give each file **the scene you chose as its cover**.

I built this by having the top AI models of three companies (Anthropic Claude Fable, OpenAI GPT Astra, Google Gemini Carbon) debate each other without holding back on tokens, and I fix annoyances as soon as I run into them while using it myself. That way you don't have to spend your own time and tokens on trial and error, and you can just use it right away.

### [⬇ Download LoveMyShelf (Windows, free)](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Windows.zip)

[User guide](GUIDE.md) · [Changelog](CHANGELOG.md)

**How your files are kept safe**

- Making covers leaves your original photos and videos as they are. Covers are always saved separately.
- Deleting only sends files to the Recycle Bin. Where there is no Recycle Bin (such as a network folder), nothing is deleted.
- When moving to another drive, the original is deleted only after the copy is verified.
- Every time the parts that move or delete files change, 41 automatic safety checks run on fake files. When a screen changes, it is also checked automatically at 150% and 225% display scaling and in a half-width window to make sure no text or button is cut off.

The download is about 220 MB, since a video player and the AI features (making covers 4× larger and sharper, finding people by face) are all inside. The AI features run only on your PC, and your files never go up to the internet.

It's free, with no ads, sign-up or paid features, and all the source code is public under GPL-3.0. No usage records are collected, and the internet is used only to check for updates, which you can turn off in Guide & Options.

## When another program is better

- You need camera RAW files: these don't open yet.
- You want full-scale photo editing such as layers or detailed retouching: quick brightness/color adjustments and erasing unwanted things are built in, but another program is better for full-scale editing.
- You want phone photos gathered automatically into the cloud: LoveMyShelf only looks at folders on your computer.
- You use a Mac or Linux: it is Windows only.

## Download and start

1. Click **Download** above and `LoveMyShelf-Windows.zip` is downloaded.
2. Right-click the ZIP file and click **Extract All** to extract everything. If you take out only part of it, videos won't play.
3. In the extracted folder, double-click `LoveMyShelf.exe`. Nothing needs to be installed.

To install it like a usual program, download the [setup file (LoveMyShelf-Setup.exe)](https://github.com/LoveMyShelf/LoveMyShelf/releases/latest/download/LoveMyShelf-Setup.exe) and double-click it. It installs for your account only, with no administrator prompt, and appears in the Start menu. Remove it in Windows Settings → Apps. Updates come through the notice, the same as the ZIP.

The first time you start it, a blue **"Windows protected your PC"** window may appear. That is because a program made by one person has no certificate. Click **More info**, then **Run anyway**.

When a new version is out, a notice pops up at the bottom right. Click it to see what changed, and click again to update. Your covers and settings stay as they are.

## Why it was made

When thousands of files pile up in a downloads or meme folder, it is hard to find what you want. Thumbnails are small and videos show some random first frame, so you end up opening them one by one.

LoveMyShelf keeps File Explorer's way of working and shows your files as big thumbnails side by side. Save a scene you like from a photo or video as a **cover**, and that file is shown by that cover from then on. A cover can be tall, square or wide.

## How it's used

The three demo videos are [in the Korean section above](#이렇게-써요).

- **Three covers from one video.** Pick the scenes you want from a video and make two JPG covers and one animated GIF cover. Point at the GIF cover in the list and it plays right away.
- **Covers follow their file.** Drag a video to another folder and its covers move with it, including the animated one.
- **Use it like File Explorer.** Folder tree, address bar, the same folder views as Explorer, drag to select, copy and paste.

## What it can do

**Browse like File Explorer**

- Open added folders, Quick access, OneDrive, This PC and the network from the folder tree on the left.
- Address bar, back, forward and up, and the same eight folder views as Explorer.
- Drag to select several, and copy or move with Ctrl+C / X / V or drag and drop.
- Right-click shows the Windows menu as it is.
- Two windows side by side, half the screen each, are comfortable for sorting.

**See at a glance**

- Photos, animated GIF/WebP and videos, large, on one screen. Name search, sorting, and filtering by files with or without covers.
- File names can be shown under the tiles in one line, two lines or in full.
- The cover collection shows covers scattered across folders in one place.

**Make covers**

- Zoom with the wheel and drag to position inside the frame, then save as JPG. One file can have several covers.
- Set the cover shape by dragging the frame's corners, using the buttons (Fill the grid's cell, Fill two cells side by side, Whole picture), or typing width and height.
- Pick the grid's cell shape (9:16, 3:4 or 1:1) and change it any time. Covers show whole inside the cells, and wide covers take two cells so they show big.
- For a video, the scenes before and after where you paused are laid out to choose from. The step between scenes can be changed (default 0.2 s).
- Animated covers are saved as GIF, WebP or MP4. Set the range and speed (0.2–4×) and preview the file that will really be saved.
- A cover can be upscaled 4× with AI. Compare the result side by side with the original cover before choosing.

**Organize files**

- Rename (F2), send to the Recycle Bin (Delete), move and copy. Covers follow the original, and Ctrl+Z undoes a move.
- Files renamed or moved in File Explorer are mostly found again by themselves.
- Even if you unplug an external drive, connect it again and its drive letter changes, the covers follow.
- Moves and copies wait in a queue and run one at a time. You can pause and resume them, and if you close and reopen the program, they continue from where they left off. If an external drive is unplugged, only that job pauses.

**Gather and find**

- Right-click a folder to see the files in it and its subfolders on one screen.
- Similar photos: finds identical files and near-identical pictures and groups them. Compare two side by side and choose which one to keep.
- Tag files and filter by several tags at once. The original files are not changed.
- Find people by face: groups similar faces found in covers. Naming a group turns it into a tag, and it runs only on your PC.

**Backup and phone**

- Locked backup: locks your covers and records with a password in a folder such as Google Drive or OneDrive, and restores them when needed (Argon2id, AES-256-GCM). Google and Microsoft can't see what's inside.
- Locked photo box: you can also lock your original photo folders with a password and keep them alongside (7-Zip AES-256). Even the file names are locked.
- Send to phone: covers are copied in order to a Google Drive or OneDrive folder so you can look at them on your phone. Originals can go too if you want.

**Watch videos**

- Seek-bar preview, scene-by-scene stepping, and keys for seeking, speed, brightness and volume.
- Capture the scene you are watching with the `/` key.
- Shows subtitles (SMI, SRT, ASS, VTT), and the subtitle explorer lets you set the sync and save it.

Light and dark themes. Korean and English.

## Computers and files

- Windows 10 or 11 (64-bit). Needs .NET Framework 4.8, which Windows 10 1903 and later already include.
- Photos: JPG (JPEG, JFIF), PNG, BMP, TIFF, GIF, WebP, HEIC (HEIF), AVIF
- Videos: MP4, M4V, MOV, MKV, WEBM, AVI, WMV, MPG (MPEG), MTS, M2TS, TS, 3GP
- Subtitles: SMI, SRT, ASS, VTT
- Not supported yet: camera RAW

## Removing it

Delete the program folder. To delete the settings and records too, paste `%LOCALAPPDATA%\LoveMyShelf` into File Explorer's address bar, press Enter, and delete the folder that opens. The covers you made stay in the cover folder.

## Contact

Send bug reports, questions or feature ideas to **lovemyshelf.app@outlook.com**.
If you have a GitHub account, you can also write in [Issues](https://github.com/LoveMyShelf/LoveMyShelf/issues).

## More

- [User guide](GUIDE.md): every feature and shortcut
- [Changelog](CHANGELOG.md): what changed in each version
- [Earlier versions](https://github.com/LoveMyShelf/LoveMyShelf/releases)
- [For developers: building from source and code layout](docs/BUILDING.md)
- License: [GPL-3.0](LICENSE). The bundled libmpv, FFmpeg, ONNX Runtime, Swin2SR, LaMa and Pretendard follow their own licenses; details are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
