# Third-party assets and design reference

The application code is MIT licensed. Bundled third-party assets retain their own licenses.

| Resource | Version / source | License file |
|---|---|---|
| Lucide static icons | `lucide-static` 0.562.0, npm registry | `buaa_agent/static/icons/LICENSE` |
| Geist variable font, Latin subset | `@fontsource-variable/geist` 5.3.0 | `buaa_agent/static/fonts/geist-LICENSE` |
| Source Serif 4 variable fonts, regular and italic | google/fonts, ofl/sourceserif4 | `buaa_agent/static/fonts/source-serif-4-LICENSE` |
| Lora variable font, Latin subset | `@fontsource-variable/lora` 5.3.0 | `buaa_agent/static/fonts/lora-LICENSE` |

Chinese text uses the operating system's available sans/serif fonts. No proprietary font files are bundled.

Visual composition is adapted from the [official DeepTutor overview screenshot](https://deeptutor.info/screenshots/OVERVIEW.png). DeepTutor's name, illustrated mascot and logo are not used as this application's identity. BUAA Learn is an independent, unofficial project; no affiliation or endorsement is implied. The reference screenshot and private QA captures are not distributed as runtime assets.

## PDF export

Image decoding and comparison use [Pillow](https://python-pillow.github.io/) (MIT-CMU); PDF generation uses [ReportLab](https://www.reportlab.com/) (BSD). Both are ordinary Python runtime dependencies.

## Frontend components

The vendored shadcn/ui Base UI components are MIT licensed; see
`frontend/LICENSE-shadcn.txt` and `frontend/*-sources.json` for source URLs and
checksums. Runtime dependencies including React, Base UI, Lucide React, and Radix
Scroll Area retain their own licenses; bundled JavaScript preserves available
license comments.

The three screenshots in `docs/images/` were selected and supplied by the author
for the README. They are documentation illustrations, not a redistributable
course-material dataset. The MIT license applies to the application code and
does not replace rights in the classroom content depicted in those screenshots.

## 桌面运行环境

桌面安装包包含 Electron（MIT）及其 Chromium、Node.js 等组件；Electron 自带的 LICENSE 与 LICENSES.chromium.html 随应用分发。登录使用 Playwright（Apache-2.0）及其 Chromium / Chrome for Testing 浏览器，浏览器许可证和 notices 保留于随附浏览器目录中。Python 运行环境保留 PSF 许可证；PyInstaller 的 GPL 许可证包含允许分发冻结应用的 bootloader 例外，详见 https://pyinstaller.org/en/stable/license.html。安装包不改变本项目 MIT 许可证。
