# Fly Photos

Fly Photos is one of the fastest photo viewers for Windows, designed as a modern replacement for the now-discontinued Google Picasa Photo Viewer.  
Built with **WinUI 3, WinRT, and Win2D**, it delivers smooth animations, instant startup, and an efficient viewing experience.  

<img width="1238" height="674" alt="image" src="https://github.com/user-attachments/assets/479fdcad-609d-47b3-9c93-5adc7d679728" />

Watch Fly Photos in action [Old Video]:  
[![Fly Photos](https://markdown-videos-api.jorgenkh.no/url?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DQkL2-WYY2Ic%26t)](https://www.youtube.com/watch?v=QkL2-WYY2Ic&t)

---

## ✨ Features
- **Fast and lightweight**
  - Instant startup with Native AOT build.  
  - In-memory and disk caching for smooth navigation even in folders with thousands of photos.  
  - Press and hold ← / → after opening a folder with thousands of photos to get a feel of Fly's performance.  

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
  - Zoom, pan, and rotate using **touchpad**, **mouse wheel**, or **keyboard**.  
  - Configurable mouse wheel behavior: zoom or navigate.  

  ### 🖐 Touchpad
  - Two-finger swipe **Left / Right** → Navigate photos  
  - Two-finger swipe **Up / Down** → Zoom In/Out or Navigate (based on mouse wheel setting)  
  - Pinch **Open / Close** → Zoom In / Out  

  ### 🖱 Mouse Wheel
  - **Wheel Scroll** → Zoom In/Out or Navigate (based on setting)  
  - **Ctrl + Wheel** → Always zoom  
  - **Alt + Wheel** → Always navigate  
  - **Tilt Wheel Left / Right** → Navigate photos (for mouses that support horizontal scroll)  
  - **Mouse Wheel on Thumbnail Strip** → Navigate photos  
  - **Mouse Wheel on On-screen Buttons** → Navigate photos / Rotate photo  
  - **Mouse Back / Forward Buttons** → Navigate photos  
  - **Click + Drag** → Pan photo  

  ### ⌨️ Keyboard
  - `← / →` → Next / Previous photo  
  - Hold `← / →` → Fly through photos super fast  
  - `↑ / ↓` → Zoom In / Out  
  - `Page Up / Page Down` → Zoom In/Out to next preset (100%, 400%, Fit)  
  - `Ctrl + + / −` → Zoom In / Out  
  - `Ctrl + Arrow Keys` → Pan photo  
  - `Home / End` → Jump to first / last photo  
  - `Ctrl + C` → Copy photo (bitmap or file)  
  - `Esc` → Close settings or exit app  

- **Windows Explorer integration**
  - Right-click an image → **Open with Fly**.  
  - Right-clicking an image shows the classic Windows Explorer context menu.  
  - Follows Explorer sort order and filtering (Recent, Search, etc.).  

---

## 🚧 Known Limitations
- v2.x supports Windows 10 and 11 only (x64).  
- SVG rendering limited to 2000px on the longest side.  
- Delete shortcut planned.  

---

## 📥 Installation
- Download the latest build from the [Releases](https://github.com/riyasy/FlyPhotos/releases) page.  
- Right-click any image and choose **Open with Fly** to start viewing.  
- On Windows 11, it may appear under **Show more options** until promoted with regular use.  
- Or set as the default app for each image file extension manually.  

---

## 🎮 Usage

| Category | Action | Shortcut |
|-----------|---------|-----------|
| **Touchpad** | Navigate photos | Two-finger Swipe Left / Right |
|  | Zoom or Navigate | Two-finger Swipe Up / Down (based on setting) |
|  | Zoom In / Out | Pinch Open / Close |
| **Mouse Wheel** | Zoom / Navigate | Wheel Scroll (based on setting) |
|  | Always Zoom | Ctrl + Wheel |
|  | Always Navigate | Alt + Wheel |
|  | Navigate Photos | Tilt Wheel Left / Right |
|  | Pan Photo | Click + Drag |
|  | Navigate Photos | Mouse Back / Forward Buttons |
|  | Navigate Photos | Wheel on Thumbnail Strip |
|  | Navigate / Rotate | Wheel on On-screen Buttons |
| **Keyboard** | Next / Previous Photo | ← / → |
|  | Fly-through Mode | Hold ← / → |
|  | Zoom In / Out | ↑ / ↓ OR Ctrl + (+ / −) |
|  | Zoom Presets | Page Up / Page Down |
|  | Pan Photo | Ctrl + Arrow Keys |
|  | Jump First / Last | Home / End |
|  | Copy to Clipboard | Ctrl + C |
|  | Exit App | Esc |

---

## 📊 Feedback
- Issues and feature requests: [GitHub Issues](https://github.com/riyasy/FlyPhotos/issues)  
- Feedback: **ryftools@outlook.com**  

---

### 🧩 Compatibility Note
Fly Photos **2.x** requires Windows 10/11 and is built with **WinUI 3 and WinRT**, providing a modern fluent UI expreriance.  
Older **1.x** versions were based on WPF and remain available only for Windows 7/8, but are no longer updated.  
