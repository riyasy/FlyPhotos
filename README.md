# Fly Photos [![Github All Releases](https://img.shields.io/github/downloads/riyasy/Flyphotos/total.svg)]()

Fly Photos is one of the fastest photo viewers for Windows, designed as a modern replacement for the now-discontinued Google Picasa Photo Viewer.  
Built with **WinUI 3, WinRT, and Win2D**, it delivers smooth animations, instant startup, and an efficient viewing experience.  

<img width="1238" height="674" alt="image" src="https://github.com/user-attachments/assets/479fdcad-609d-47b3-9c93-5adc7d679728" />

---

Watch Fly Photos in action:  

[![Fly Photos](https://markdown-videos-api.jorgenkh.no/url?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DHi0H9k67kvA)](https://www.youtube.com/watch?v=Hi0H9k67kvA)

---

## 📥 Download

<a href="https://get.microsoft.com/installer/download/9pmsk128v1qt?referrer=appbadge" target="_self" >
	<img src="https://get.microsoft.com/images/en-us%20light.svg" width="200"/>
</a>

### Fly Photos is available in the Microsoft Store
- Purchasing Fly Photos from the Microsoft Store is the best way to support the ongoing development of the project.
- You can also support via a donation at [♥ GitHub Sponsors](https://github.com/sponsors/riyasy). After donating, please email **ryftools@outlook.com**, and I will send you a Store promo code
- The Store version offers fast, easy installation across all your Windows devices and includes fully automatic, behind-the-scenes updates for the latest features and fixes.

### Difference Between GitHub Release and Store Release
|  | [Microsoft Store](https://apps.microsoft.com/detail/9pmsk128v1qt?launch=true&cid=GitHubRelease&mode=full) | GitHub MSI | 
| -- | -- | -- |
| **Price** | 🪙 Paid | 🆓 Free |
| **Updates** | ✅ Seamless auto-updates | ❌ User-managed |
| **Bug Fixes** | ✅ Can be available faster | ❌ Available in official releases only |
| **Security** | ✅ Signed and certified by Microsoft | ❌ Not signed |

---
## 📥 Installation

- Option 1 : [**Install from Microsoft Store**](https://apps.microsoft.com/detail/9pmsk128v1qt?launch=true&cid=GitHubRelease&mode=full)
- Option 2 : Download and install MSI from Github [**Releases Page**](https://github.com/riyasy/FlyPhotos/releases)


## 🚀 Getting Started

Once installed, these are the ways to use Fly to view photos

### 1. Context Menu (Right-Click)
- **MSI / GitHub Version:** Right-click an image and select **"Open with Fly"** (located in the classic context menu).
- **Store Version:** Right-click an image, select **"Open with"**, and choose **Fly Photos** from the list.

### 2. Standalone Mode
Launch **Fly Photos** directly from the Start menu. You can then use the file picker to browse and select a folder or image to view.

### 3. Set as Default App
To open images with Fly Photos automatically when you double-click them:
1. Right-click an image file (e.g., a `.jpg`).
2. Select **"Open With"** and **"Choose Another App"** and Select **FlyPhotos** from List and click **Always**.
3. Repeat this for other file types (PNG, WEBP, etc.) as needed.

---

## ✨ Features
- **Fast and lightweight**
  - Instant startup with Native AOT build.  
  - In-memory and disk caching for smooth navigation even in folders with thousands of photos.  
  - Press and hold `←` / `→` after opening a folder with thousands of photos to get a feel of Fly's performance.  
  - Tight Explorer integration. Follows Explorer sort order and filtering (Recent, Search, etc.).  

- **Image format support**
  - All formats supported by Windows Imaging Component (JPEG, PNG, TIFF, RAW, etc.).  
  - Extended support for PSD (with transparency), HEIC/HEIF, SVG, GIF, and APNG (Animated PNG).  

- **Viewing experience**
  - Transparent background like in Picasa Photo Viewer.  
  - Modern Windows themes like Mica, Acrylic, and Frozen Glass.  
  - Smooth pan and zoom animations.  
  - Thumbnail strip with adjustable size and click-to-jump navigation.  
  - Multi-monitor support (remembers last used monitor).  

- **Controls**
  - **Versatile Inputs:** Zoom, pan, and rotate using **Touchpad gestures**, **Mouse**, or **Keyboard**.
  - **Customizable:** Configurable mouse wheel behavior (Zoom vs. Navigate).
  - **Touch Friendly:** Native support for pinch-to-zoom and two-finger swipe navigation.

---

## 🎮 Usage

| Category | Action | Shortcut |
|-----------|---------|-----------|
| **🖐 Touchpad** | Navigate photos | Two-finger Swipe Left / Right |
|  | Zoom or Navigate | Two-finger Swipe Up / Down (based on setting) |
|  | Zoom In / Out | Pinch Open / Close |
| **🖱 Mouse** | Zoom / Navigate | Wheel Scroll (based on setting) |
|  | Always Zoom | Ctrl + Wheel |
|  | Always Navigate | Alt + Wheel |
|  | Navigate Photos | Tilt Wheel Left / Right |
|  | Pan Photo | Click + Drag |
|  | Navigate Photos | Mouse Back / Forward Buttons |
|  | Navigate Photos | Wheel on Thumbnail Strip |
|  | Navigate / Rotate | Wheel on On-screen Buttons |
| **⌨️ Keyboard** | Next / Previous Photo | ← / → |
|  | Fly-through Mode | Hold ← / → |
|  | Zoom In / Out | ↑ / ↓ OR Ctrl + (+ / −) |
|  | Zoom Presets | Page Up / Page Down |
|  | Pan Photo | Ctrl + Arrow Keys |
|  | Multi-page TIFF Nav | Alt + Left / Right |
|  | Jump First / Last | Home / End |
|  | Delete Photo | Del |
|  | Show Properties | D |
|  | Copy to Clipboard | Ctrl + C |
|  | Exit App | Esc |

---

## 🚧 Known Limitations
- SVG rendering limited to 2000px on the longest side.  
- HDR Support yet to be implemented
- Max Resolution of the images which can be displayed depends on DirectX device capability

---

## 📊 Feedback
- Issues and feature requests: [GitHub Issues](https://github.com/riyasy/FlyPhotos/issues)  
- Feedback: **ryftools@outlook.com**  

---

### 🧩 Compatibility Note
Older **1.x** versions were based on WPF and remain available only for Windows 7/8, but are no longer updated.  
