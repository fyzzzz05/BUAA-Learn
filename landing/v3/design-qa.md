# BUAA Learn landing page QA

final result: passed

## First-screen background scope — 2026-11-16
- User requested the background only on the first screen. Moved both light texture and glyph field into an isolated, clipped opening section, with a full-width bottom divider and a soft fade.
- Later sections use the clean base background. Background pause control moved to the top navigation; IntersectionObserver suspends the renderer when the opening section is out of view.
- Native ego browser screenshot `qa/first-screen-divider.png` verifies animated field above the divider, clean product sections below, and correct section spacing. Build passed.
- Offscreen renderer scheduling reviewed in code; no instrumentation claim.

## Previous iteration — 2026-11-16
- Scope: continuous linear playback, action-led scenes, and the user-selected Henry Fan glyph-wind background. This section supersedes earlier layout/interaction notes below.
- Replaced jump-between-chapters interval with a 30-second requestAnimationFrame timeline. Chapter selection seeks to the chapter start; pause retains progress; resume continues; the end stops at 100%; replay restarts. Browser samples moved from 0.887667% to 22.941% over 6.6 seconds, then remained at 23.0247% while paused. End state verified as 00:30 / 00:30 with Play control.
- Removed duplicate homework panel and oversized statement. One overview row, followed by two aligned details; mobile stacks the same three actions.
- Desktop 1440px, intermediate 946px, and mobile 390px layouts were visually inspected. Fixed playhead end overflow; verified document width equals viewport. Source modal, overview selection and homework checkbox passed.
- Background uses the selected MIT-licensed implementation, with deep blue ink, pointer wakes and circular click ripples. Licence is shipped with the production build.
- WebGL canvas reached ready/moving in IAB; final lower brightness, native browser rendering, click ripple and pause/resume button verified in ego lite via native CUA after IAB became unavailable. The native screenshot shows legible foreground UI with the field behind it.
- Evidence: qa/glyph-hero.png and qa/glyph-scenes.png (native browser), qa/tablet-v2.png and qa/mobile-v2.png (layout before background integration).
- Build passed; packaging tests 4/4 passed.
- Limits: final background was not re-tested at mobile viewport after IAB connection became unavailable. Reduced-motion and WebGL-unavailable fallback were reviewed in code, not simulated. No claim of final mobile GPU/performance or cross-browser testing.
- No remaining observed P0/P1/P2 visual or interaction issues. Local only, not deployed.


## Source and evidence
- Source: `/var/folders/ny/4gwv7dzx4nj3f3jk748v_9x80000gn/T/codex-clipboard-1db29470-8508-4694-9e9c-fa14ca4f6d79.png` (946 × 1663).
- Implementation: `qa/desktop.png` (946 × 1919), local `http://127.0.0.1:8784/`.
- Desktop CSS viewport: 946 × 900, DPR 1; full-page capture. Also checked 1440 × 1000 and 390 × 844.
- Same initial selected topic 12:56, paused; first homework checked. Mobile capture includes the intentionally tested second checkbox selection.
- Full-view paired comparison: `qa/comparison.png`. Earlier comparison: `/tmp/buaa-compare.png`.
- Focused paired hero/timeline comparison: `qa/detail.png`.
- Mobile: `qa/mobile.png`.

## Comparison history
1. Initial desktop render: P2 heading hierarchy weaker than selected reference; P2 course-directory column wrapped excessively and inflated demo rows. Increased headline size/weight, section headings, widened directory, adjusted demo columns and reduced padding. Corrected document title and language.
2. Captured revised desktop and compared against supplied source at equal width. Core arrangement retained: left aligned hero, white note/source panel, full-width chapter ruler, statement plus homework, three action rows, download section. No remaining blocking P0/P1/P2 issues.
3. Shared homework demo state to avoid contradictory checkmarks across the same sample task list; verified both instances update.

## Required surfaces
- Typography: system PingFang SC / Microsoft YaHei, heavy display headings and readable DOM text. Close visual hierarchy; OS text rendering differs from generated lettering. Hero wraps on two deliberate lines at desktop.
- Layout: same section sequence and two-column rhythm; extra height comes from functional footer, version/platform details, disclosure and modest extra reading space. Mobile reflows to one column with no horizontal overflow. All timeline controls remain visible.
- Colors: deep ink background, cobalt selected chapter/CTA, light slate product surfaces. Generated light texture uses matching direction but a softer beam than reference. No full-page raster substituted for interface.
- Assets: generated raster light background, Phosphor library icons. Paper-plane library mark replaces ambiguous generated mark; window headers use book icon instead of artificial OS traffic lights. Interface/timeline/checkboxes are functional HTML controls.
- Copy: preserves headline and three student actions. Replaces unsupported “automatic editing” claim with timeline review, uses polished sample content, replaces date decoration with working navigation. Adds API cost/login/runtime conditions in download area.

## Browser checks
- Topic buttons update selected chapter, note, source and playhead.
- Quote opens native modal, Escape closes it.
- Play progresses from 07:34 to 12:56; pause and replay work.
- Homework checkbox toggles and synchronizes both demo instances.
- Header/hero download CTA navigates to platform links. URLs retained from existing project; installer downloads not executed.
- 390px and 1440px: no horizontal document overflow.
- Browser error/warning logs empty in inspected session.
- Production build passed. `git diff --check` passed.
- Reduced-motion styling and media query handling present; system setting not changed during QA.

## Follow-up polish
- P3: fine-tune background lighting intensity after user's visual review.
- Demo content is illustrative, not connected to a live classroom account. Not deployed.

## Cursor lighting refresh — 2026-11-17
- Added continuous mouse pointermove lighting with eased cursor tracking, a wider trailing glow, and fade-out on pointer leave. The Canvas2D layer runs independently of click ripples.
- Increased glyph wake sensitivity and blue highlight strength. Replaced the hard diagonal hero beam with imagegen-created dark navy atmospheric texture (hero-atmosphere-v2.png).
- Both effects remain inside the clipped first-screen wrapper. Pausing, reduced motion, offscreen state, hidden document, and cleanup stop or clear the light layer.
- Production build and all 4 existing packaging tests passed. Native ego browser displayed the new first-screen texture and page content; the inspected console contained no error message.
- Live movement/pause verification was interrupted when native browser window access became unavailable. Do not treat this pass as verification of continuous hover motion, mobile GPU performance, or the final visual strength.

## Product workflow animation — 2026-11-17
- Replaced the lower static product panels with a 28-second DOM-rendered BUAA Learn workflow: review the classroom timeline, type a knowledge-point query, reveal the matching original statement with surrounding context, then check off two homework items.
- The approved first-screen markup, background and playback remain unchanged. The lower demonstration uses polished fictional classroom content and the user's book icon.
- Added pause/resume, replay and three chapter selectors. Animation time pauses offscreen or in a hidden tab; reduced-motion users get selectable still scenes. Pointer positioning updates when the source pane changes layout, and hides when its target is absent on small screens.
- Native Chrome screenshots observed during this pass show the recap, source pane, paused control, and final homework state (2/3 completed). Chapter switching and playback were observed. These screenshots were returned inline by the UI tool, not saved as local QA assets.
- Production build passed; existing Sites packaging tests passed 4/4. No deployment performed.
- Limits: native UI access failed with noWindowsAvailable during responsive emulation setup. Final mobile layout and reduced-motion behavior were reviewed in code but not visually verified; replay/pause timing was not measured in this pass. No current cross-browser or mobile performance claim. Earlier QA entries describe earlier iterations.
