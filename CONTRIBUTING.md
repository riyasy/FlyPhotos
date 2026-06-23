# Contributing to Fly Photos


## 1. Reporting Bugs

A good bug report includes:

- Fly Photos version, and whether it's the **Microsoft Store** or **GitHub MSI** build.
- Windows version and architecture (x64 / ARM64).
- The image format involved (e.g. JPEG, HEIC, PSD, AVIF) and, if possible, a sample file.
- Steps to reproduce, what you expected, and what actually happened.
- Screenshots or a short screen recording when the issue is visual.

---

## 2. Development

### Quick build notes

Fly Photos is WinUI 3 + Win2D on **.NET 10** with Native AOT, plus native C++ and a Rust bridge. You need **Visual Studio 2022**, the **.NET 10 SDK**, **vcpkg**, and **Rust/cargo**.


### Guidelines

- Match the style of the surrounding code, and keep PRs focused.
- Be mindful of allocations and threading in the navigation/ rendering / image-loading hot paths.
- Branch off `main`, test your change with real image files, and reference any related issue (e.g. `Ref #123`) in the PR.

### AI-Assisted Contributions

If you're vibe coding, please ensure a human has reviewed and understands the entire change before opening a PR. Automated or unattended submissions are not welcome.

- Please review and understand every line of code you submit.
- Discuss significant architectural changes in an issue before implementing them.
- Ensure your contribution provides more value than the effort required to review it.
- If AI tools helped with code, documentation, commit messages, or the PR description, please mention it in the PR description.

---

## 3. Localization

Help keeping translations accurate and adding new languages is especially welcome. **You don't need to build the app** to contribute a translation — it's just an XML file edit.

### Where the strings live

Each language is a `Resources.resw` file under `Src/FlyPhotos/Strings/<locale>/`:

```
Src/FlyPhotos/Strings/
├── en-US/Resources.resw   ← English, the source of truth
├── de-DE/Resources.resw
├── fr-FR/Resources.resw
└── ...
```

Each string is a `<data>` entry — keep the `name` untouched and translate the `<value>`:

```xml
<data name="Settings_Theme" xml:space="preserve">
  <value>Theme</value>
</data>
```

### Improving an existing translation

1. Open the `Resources.resw` for the language under `Src/FlyPhotos/Strings/<locale>/`.
2. Compare against `en-US/Resources.resw` and fix or fill in any `<value>`.
3. **Never change the `name` attributes** — they are the lookup keys and must match `en-US`.
4. Save as **UTF-8** and open a pull request.

### Adding a new language

1. Copy `Src/FlyPhotos/Strings/en-US/Resources.resw` into a new `Strings/<locale>/` folder (use the correct BCP-47 code, e.g. `cs-CZ`).
2. Translate every `<value>`, leaving the `name` keys as-is.

---

Thank you for helping make Fly Photos better! 🚀
