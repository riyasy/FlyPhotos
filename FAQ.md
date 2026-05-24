# Fly Photos — Frequently Asked Questions

---

## 1. My browser is flagging the Fly Photos installer (.msi). Why?

This is expected for small independent apps and is not specific to Fly Photos. Here is why it happens.

### What is code signing?

Code signing is a process where a software publisher purchases a digital certificate from a certificate authority and uses it to sign their installer. Windows and browsers use these signatures to verify the publisher's identity and build a "reputation" score for the file over time. Certificates can cost hundreds of dollars per year, which is a significant expense for a free, independently developed app. Without a certificate or with a new certificate that has not yet built up download reputation, browsers like Chrome and Edge will show a warning even for completely safe files.

### What can you do?

**Option 1 — Trust the file and install it**
If you downloaded Fly from the [official GitHub releases page](https://github.com/riyasy/FlyPhotos/releases), the file is safe. You can override the browser warning:
- In **Chrome / Edge**: click the download item, choose **Keep** (or **Keep anyway**).
- In **Windows SmartScreen**: click **More info** → **Run anyway**.

**Option 2 — Install from the Microsoft Store**
The Store version of Fly is signed and certified by Microsoft. If your browser restrictions or organization policy prevent you from running unsigned installers, install Fly from the Microsoft Store instead. No warnings, no exceptions needed and lot of benefits like supporting the developer, automatic updates, fast release channel.

---

## 2. What is the proper way to install the MSI?

If Fly Photos is already installed on your PC, **uninstall it first** before running the new installer. This is the cleanest upgrade path. Your settings are preserved — uninstalling Fly does not touch them.

### Files Fly leaves behind after uninstall

The uninstaller removes the application itself but intentionally leaves your personal data so settings survive upgrades. If you are removing Fly permanently and want a clean slate, delete these folders manually:

| What | Path |
|---|---|
| Settings & disk cache | `%LOCALAPPDATA%\FlyPhotos\` |
| Log files | `%TEMP%\FlyPhotos\` |

You can paste either path directly into the Windows Explorer address bar or the Run dialog (`Win + R`) to open it.
NOTE : The Store version of Fly always uninstall without leaving a trace.

---

## 3. How do I set Fly as the default viewer for a file type?

Both the **Store version** and the **MSI (installer) version** of Fly register themselves as a capable handler for all supported image formats. This means Fly will appear in Windows' "Open with" list for those formats automatically after installation.

### Method 1 — Open with (quickest)

1. Right-click any image file in File Explorer.
2. Choose **Open with** → **Choose another app**.
3. Select **Fly Photos** from the list.
4. Check **Always use this app to open `.<ext>` files**.
5. Click **OK**.

Repeat for each file type you want Fly to handle.

### Method 2 — Windows Settings

1. Open **Settings** → **Apps** → **Default apps**.
2. Scroll down and click **Choose defaults by file type**.
3. Search for the extension (e.g. `.jpg`, `.heic`, `.webp`).
4. Click the current default app shown next to it and select **Fly Photos**.

This is the most reliable method when setting defaults for many formats at once.

### Supported file types

The list below covers all formats Fly is registered to handle. If your system has additional WIC codecs installed (e.g. third-party RAW codecs, JPEG XL codecs), Fly can open those formats too even if they are not listed here.

**Common image formats**
`.jpg` `.jpeg` `.jpe` `.jfif` `.png` `.bmp` `.dib` `.rle` `.gif` `.tif` `.tiff` `.ico` `.icon` `.cur` `.webp` `.svg` `.psd`

**HEIF / AVIF family**
`.heic` `.heif` `.hif` `.heics` `.heifs` `.avci` `.avcs` `.avif` `.avifs`

**Modern formats**
`.jxl` `.jxr` `.wdp` `.dds`

**RAW camera formats**
`.arw` `.cr2` `.cr3` `.crw` `.dng` `.nef` `.nrw` `.orf` `.ori` `.raf` `.raw` `.rw2` `.rwl`
`.3fr` `.ari` `.bay` `.cap` `.dcs` `.dcr` `.drf` `.eip` `.erf` `.fff` `.iiq` `.k25` `.kdc`
`.mef` `.mos` `.mrw` `.pef` `.ptx` `.pxn` `.sr2` `.srf` `.srw` `.x3f`

**Specialist / ImageMagick formats**
`.exr` `.hdr` `.tga` `.pct` `.pict` `.pcx` `.dpx` `.cin` `.qoi` `.sgi` `.rgb` `.rgba`
`.pbm` `.pgm` `.ppm` `.pnm` `.pam` `.pfm` `.pix` `.ras` `.sun` `.ff`

---

## 4. How do I get "Open with Fly" or "Open with → FlyPhotos" on the right-click menu?

There are two different things that can appear in the Windows right-click menu, and it is easy to confuse them:

| What you see | How it works | Available on |
|---|---|---|
| **Open with → FlyPhotos** | Standard Windows "Open with" submenu — Windows shows all registered handlers for that file type | Both Store and MSI |
| **Open with Fly** | A top-level entry directly in the classic context menu (may not be visible in Windows 11 compact menu) | MSI only |

Both let you open a file in Fly.

### "Open with → FlyPhotos" (both versions)

After installing either the Store or MSI version, Fly registers itself as a handler for all supported image formats. Windows automatically adds it to the **Open with** submenu when you right-click a supported image. No extra steps needed.

### "Open with Fly" entry (MSI version only)

The MSI installer includes an optional checkbox that adds a dedicated **Open with Fly** entry directly in the classic right-click menu — no submenu required.

**To enable it during installation**, when running the Fly Photos installer (`.msi`), look for the option **Add generic "Open With Fly" entry in Windows Classic Context Menu**.
Warning : This adds the menu for all file types not just images.

If Fly is already installed and you want to add or remove this entry, uninstall and reinstall with the option toggled accordingly.

---

## 5. Fly is not displaying my image. Why?

### Step 1 — Check if the format is supported

Open Fly's **Settings** and go to the **second tab** (Codecs / Formats). Check whether your file's format is listed as supported.

**If the format is supported** but the image still does not display, please raise a ticket on [GitHub](https://github.com/riyasy/FlyPhotos/issues) or send a mail to [ryftools@outlook.com](mailto:ryftools@outlook.com) with details.

**If the format is not supported**, Fly may still be able to display it if a Windows WIC codec for that format is installed on your system. Search the internet for a WIC codec for your format, install it, and Fly will pick it up automatically.

### Animated images — special note

Fly currently supports animation only for the following formats:

- GIF (`.gif`)
- Animated PNG (`.png`)
- Animated WebP (`.webp`)
- Animated AVIF (`.avif`)

If you need support for a different animated format, please raise a ticket on [GitHub](https://github.com/riyasy/FlyPhotos/issues) or mail the developer at [ryftools@outlook.com](mailto:ryftools@outlook.com).

---

## 6. My animated AVIF is not playing

The `.avifs` extension is a non-standard and rarely supported variant. Fly does **not** support `.avifs`, but it fully supports animated AVIF saved as `.avif`.

If you have an animated AVIF file, save or rename it with the `.avif` extension. Fly will automatically detect the animation and play it correctly.

---

## 7. How do I install or uninstall the Windows native RAW codec?

### Uninstall

Open PowerShell as Administrator and run:

```powershell
Get-AppxPackage *RawImageExtension* | Remove-AppxPackage -AllUsers
```

### Install

Download **Raw Image Extensions** from the Microsoft Store:
[https://apps.microsoft.com/detail/9nctdw2w1bh8](https://apps.microsoft.com/detail/9nctdw2w1bh8)

---

## 8. HE/HE* Nikon NEF RAW files from Nikon Zf, Z8 etc. are not displaying correctly

This is a known limitation with these files. The camera writes a proprietary high-efficiency compressed RAW format that generic decoders cannot fully decode.

**Your options in Fly:**

1. **Use the embedded preview** — Fly extracts the full-quality JPEG embedded inside the RAW file. This is fast and reliable.
   To make sure Fly uses the embedded JPEG rather than attempting to decode the raw data, open **Settings → General tab → Advanced section** and turn off the **Decode Camera RAW** toggle.
2. **Install the official Nikon NEF codec for Windows** — Note that even with the codec installed, the rendered image is typically identical to the embedded JPEG, so the practical difference is minimal.

### Install the Nikon NEF Codec

Download directly from Nikon:
[S-NEFCDC-013104WF-ALLIN-ALL___.exe](https://download.nikonimglib.com/archive7/5BLiL00geYdA06vPEVw70TjsbQ23/S-NEFCDC-013104WF-ALLIN-ALL___.exe)

### Further Reading

Community discussion on this topic:
[r/Nikon — Nikon RAW file codec for Windows 11](https://www.reddit.com/r/Nikon/comments/1kyslcq/nikon_raw_file_codec_for_windows_11/)

---

## 9. Fly is using more memory than expected. How do I reduce it?

Fly pre-loads photos before and after the currently viewed image into memory so navigation feels instant. You can reduce how many are kept by adjusting the cache sizes in **Settings → General tab → Caching** section.

There are two sliders:

- **Low-resolution cache** — controls how many low-res previews are kept in memory on each side of the current photo (default 300, minimum 50). Reducing this has the biggest impact on overall memory use.
- **High-resolution cache** — controls how many full-quality images are kept in memory on each side (default 2, minimum 1).

Set both sliders to their minimum values for the lowest memory footprint, at the cost of slightly slower navigation when jumping between photos.

---

## 10. Right-clicking inside Fly causes a crash

This is an extremely rare issue that was reported by users who use third-party tools to style or theme the Windows UI and context menus. It does not occur under normal Windows usage. The issue has been fixed in recent versions of Fly.

If you are still seeing it, see [issue #37](https://github.com/riyasy/FlyPhotos/issues/37) for details — in particular [this comment](https://github.com/riyasy/FlyPhotos/issues/37#issuecomment-3619370161) has a workaround.

---

## 11. I want Fly Photos in my language. How?

**If you have basic technical skills and know English**, take a look at the English resource file on GitHub:
[Strings/en-US/Resources.resw](https://github.com/riyasy/FlyPhotos/blob/main/Src/FlyPhotos/Strings/en-US/Resources.resw)

It contains all the text displayed in Fly. Translate the values into your language and either:
- Send the translated file to [ryftools@outlook.com](mailto:ryftools@outlook.com), or
- Submit a Pull Request on GitHub.

**If that sounds too technical**, raise a ticket on [GitHub](https://github.com/riyasy/FlyPhotos/issues) and the developer will send you an online spreadsheet you can fill in directly.
