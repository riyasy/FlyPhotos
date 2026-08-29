# Fly Photos [![Github All Releases](https://img.shields.io/github/downloads/riyasy/FlyPhotos/total.svg)]()

Fly Photos is one of the fastest photo viewers for Windows, designed as a modern replacement for the now-discontinued Google Picasa Photo Viewer.  
Built with **WinUI 3, WinRT, and Win2D**, it delivers smooth animations, instant startup, and an efficient viewing experience.  

<img width="1238" height="674" alt="image" src="https://github.com/user-attachments/assets/479fdcad-609d-47b3-9c93-5adc7d679728" />

---

Watch Fly Photos in action:  

[![Fly Photos](https://markdown-videos-api.jorgenkh.no/url?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DncWzt-ZoIq4)](https://www.youtube.com/watch?v=ncWzt-ZoIq4)

---

## 📥 Download

<a href="https://apps.microsoft.com/detail/9pmsk128v1qt?referrer=appbadge&mode=full" target="_blank"  rel="noopener noreferrer">
	<img src="https://get.microsoft.com/images/en-us%20light.svg" width="200"/>
</a>


- Purchasing Fly Photos from the Microsoft Store is the best way to support the ongoing development of the project.
- You can also support via a donation at [![](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/riyasy) . After donating, please email **ryftools@outlook.com**, and I will send you a Store promo code

### Difference Between GitHub Release and Store Release
|  | [Microsoft Store](https://apps.microsoft.com/detail/9pmsk128v1qt?launch=true&cid=GitHubRelease&mode=full) | GitHub MSI | 
| -- | -- | -- |
| **Price** | 🪙 Paid | 🆓 Free |
| **Updates** | ✅ Seamless auto-updates | ❌ User-managed |
| **Security** | ✅ Signed and certified by Microsoft | ❌ Not signed |

Both builds are the same app, with the same features.

---

## 📦 Installation

- Option 1 : [**Install from Microsoft Store**](https://apps.microsoft.com/detail/9pmsk128v1qt?launch=true&cid=GitHubRelease&mode=full)
- Option 2 : Download the MSI for your architecture (x64 or ARM64) from the GitHub [**Releases Page**](https://github.com/riyasy/FlyPhotos/releases)

**Requires** Windows 10 version 1809 (build 17763) or newer, on x64 or ARM64.


## 🚀 Getting Started

Once installed, you can open photos in three ways:

### 1. Context Menu (Right-Click)
Right-click an image, select **"Open with"**, and choose **Fly Photos** from the list.

### 2. Standalone Mode
Launch **Fly Photos** directly from the Start menu, then use the file picker to browse to a folder or image.

### 3. Set as Default App
To open images with Fly Photos automatically when you double-click them:
1. Right-click an image file (e.g., a `.jpg`).
2. Select **"Open With"** → **"Choose Another App"**, pick **FlyPhotos**, and click **Always**.
3. Repeat for other file types (PNG, WEBP, etc.) as needed.

> Tip: to change many types at once, go to **Windows Settings → Apps → Default apps → FlyPhotos** and set the file types there.

---

## ✨ Features

- **Fast and lightweight**
  - Instant startup with a Native AOT build.
  - In-memory and disk caching for smooth navigation even in folders with thousands of photos.
  - **Fly-through mode** - press and hold `←` / `→` and glide through a large folder with no loading spinners.
  - Tight Explorer integration. Follows Explorer's sort order and filtering (Recent, Search, etc.).

- **Image format support**
  - Everything the Windows Imaging Component handles (JPEG, PNG, TIFF, BMP, camera RAW, and any format you have a WIC codec installed for).
  - Extended support for PSD (with transparency), HEIC/HEIF, AVIF, SVG, GIF, APNG, animated WebP, and DDS.
  - Camera RAW with a configurable decoder order - Rawler (fastest), WIC (system codecs), or ImageMagick (best compatibility) - plus an option to decode the real RAW data instead of the embedded JPEG.
  - Multi-page TIFF and multi-frame ICO, with page-by-page navigation.

- **Viewing experience**
  - Transparent background like Picasa Photo Viewer, or Mica, Acrylic, Frozen Glass, or a solid colour of your choice.
  - Light, dark, or system theme.
  - Smooth pan, zoom, and rotation, with optional sticky zoom stops.
  - Thumbnail strip with adjustable size, selection colour, and optional animation.
  - Photo info panel with EXIF and camera metadata.
  - Choose whether pan/zoom/rotation resets, is remembered per photo, or carries over during navigation.
  - Multi-monitor support (remembers the last used monitor); starts maximized, full screen, or in its previous window state.

- **Controls**
  - **Fully customizable keyboard shortcuts** - rebind almost any command in **Settings → Keyboard**.
  - **Configurable mouse** - wheel, wheel click, right-click-and-hold, and the back/forward buttons are all set in **Settings → Mouse**.
  - **Precision touchpad** - two-finger swipe to navigate and pinch to zoom.
  - Right-click opens the real Windows Explorer context menu.

- **Localized**
  - Available in 20 languages, including right-to-left layout for Arabic.

---

## 🎮 Usage

### ⌨️ Keyboard

These are the **defaults**. Every command below except `Esc` and `Del` can be rebound in **Settings → Keyboard**, and any shortcut you change is stored per user, so future releases keep your bindings.

| Group | Command | Default |
|--|--|--|
| **Navigation** | Next / previous photo | `→` / `←` |
| | Fly-through mode | Hold `→` / `←` |
| | First / last photo | `Home` / `End` |
| | Next / previous page (multi-page TIFF, multi-frame ICO) | `Alt` + `→` / `←` |
| **Zoom and pan** | Zoom in / out | `↑` / `↓`, or `Ctrl` + `+` / `−` |
| | Step zoom (fit → 100% → 400%) | `Page Up` / `Page Down` |
| | Actual size (1:1) | `A` |
| | Fit to window | `F` |
| | Pan | `Ctrl` + arrow keys |
| **Rotate** | Rotate left / right | `L` / `R` |
| **View** | Full screen | `F11` |
| | Maximize or restore | `Enter` |
| | Photo info panel | `I` |
| | Close FlyPhotos | `Esc` |
| **File** | Copy photo | `Ctrl` + `C` |
| | Delete photo | `Del` |
| | Rename photo | `F2` |
| | Print photo | `P` |
| | Share photo | `S` |
| | Open file location | `W` |
| | File properties | `Alt` + `Enter` |
| | File details | `D` |
| | More actions menu | `M` |
| **Open with** | Open in external app 1–4 | `Ctrl` + `1`–`4` |
| | Open-with panel | `E` |

### 🖱 Mouse

Rows marked **configurable** can be changed in **Settings → Mouse**; the values shown are the defaults.

| Action | Default behaviour | |
|--|--|--|
| Wheel scroll | Zoom in / out | configurable (zoom or navigate) |
| `Ctrl` + wheel | Always zooms | fixed |
| `Alt` + wheel | Always navigates | fixed |
| Tilt wheel left / right | Navigate photos | fixed |
| Wheel click | Toggle full screen | configurable |
| Back / forward buttons | Navigate photos | configurable (navigate or step zoom) |
| Left click + drag on photo | Pan the photo | fixed |
| `Ctrl` + drag | Move the window | fixed |
| Left click outside the photo | Restore the window | configurable |
| Double click on photo | Actual size ↔ fit to window | fixed |
| Double click outside the photo | Maximize the window | follows the click-outside setting |
| Right click | Windows Explorer context menu | fixed |
| Right click + hold | Zoom in | configurable |
| Wheel over the thumbnail strip | Navigate photos | fixed |
| Wheel over the on-screen rotate button | Rotate the photo | fixed |

### 🖐 Precision touchpad

| Action | Gesture |
|--|--|
| Navigate photos | Two-finger swipe left / right |
| Zoom or navigate | Two-finger swipe up / down (follows the wheel setting) |
| Zoom in / out | Pinch open / close |

---

## 🚧 Known Limitations
- SVG rendering is capped at 2000 px on the longest side.
- HDR photos are displayed tone-mapped to SDR; true HDR output is not implemented yet.
- Very large images (roughly >16384 px) may not display on all hardware, due to DirectX texture size limits.
- Multiple instances is still beta: extra instances only show the selected image, with no navigation, delete, or settings.

---

## 📊 Feedback
- Issues and feature requests: [GitHub Issues](https://github.com/riyasy/FlyPhotos/issues)  
- Feedback: **ryftools@outlook.com**  

---

### 🧩 Compatibility Note
Older **1.x** versions were based on WPF and remain available only for Windows 7/8, but are no longer updated.  
