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
  - Zoom, pan, and rotate using mouse, keyboard, or scroll wheel.  
  - Configurable mouse wheel behavior: zoom or navigate.  
  - Shortcuts:  
    - `← / →` : Next / Previous photo  
    - Hold `← / →` : Fly through photos super fast  
    - `Mouse Wheel` : Zoom or Navigate (based on setting)  
    - `Ctrl + Mouse Wheel` : Always zoom  
    - `Alt + Mouse Wheel` : Always navigate  
    - `Ctrl + + / −` : Zoom in / out  
    - `Ctrl + Arrow Keys` : Pan photo  
    - `Ctrl + C` : Copy photo (bitmap or file)  
    - `Home / End` : Jump to first / last photo  
    - Click + Drag : Pan photo  
    - `Mouse Wheel` on `Thumbnail bar` : Navigate  
    - `Mouse Wheel` on `On-screen Next / Previous button` : Navigate  
    - `Mouse Wheel` on `On-screen Rotate button` : Rotate  

- **Windows Explorer integration**
  - Right-click an image → **Open with Fly**.  
  - Right-clicking an image shows the classic Windows Explorer context menu.  
  - Follows Explorer sort order and filtering (Recent, Search, etc.).  

---

## 🚧 Known Limitations
- v2.x supports Windows 10 and 11 only (x64).  
- SVG rendering limited to 2000px on the longest side.  
- Touchpad gestures and delete shortcut are planned.  

---

## 📥 Installation
- Download the latest build from the [Releases](https://github.com/riyasy/FlyPhotos/releases) page.  
- Right-click any image and choose **Open with Fly** to start viewing.  
- On Windows 11, it may appear under **Show more options** until promoted with regular use.  
- Or set as the default app for each image file extension manually.  

---

## 🎮 Usage
Keyboard and mouse shortcuts:  

| Action | Shortcut |
|--------|----------|
| Next / Previous photo | ← / → |
| Fly-through mode | Hold ← / → |
| Zoom In / Out | Mouse Wheel / Ctrl + + / − |
| Pan | Click + Drag / Ctrl + Arrow Keys |
| Rotate | Mouse Wheel on On-screen Rotate button |
| Fit to Screen | Double-click |
| 1:1 Zoom | Button / Small images auto-1:1 |
| Jump First / Last | Home / End |
| Copy to Clipboard | Ctrl + C |
| Navigate via Thumbnails | Mouse Wheel on Thumbnail bar |
| Navigate via On-screen Buttons | Mouse Wheel on Next / Previous button |
| Rotate via On-screen Button | Mouse Wheel on Rotate button |

---

## 📊 Feedback
- Issues and feature requests: [GitHub Issues](https://github.com/riyasy/FlyPhotos/issues)  
- Feedback: ryftools@outlook.com  

---

### Compatibility Note
Fly Photos **2.x** requires Windows 10/11 and is built with **WinUI 3 and WinRT**, providing GPU acceleration and modern caching.  
Older **1.x** versions were based on WPF and remain available only for Windows 7/8, but are no longer updated.  
