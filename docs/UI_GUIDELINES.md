# UI guidelines for the TcAgent windows and dialogs

Every tool window and dialog of the TcAgent extension (`vsbridge/`) follows these rules - the TcAgent Knowledge,
TcUnit Tests and TcAgent Chat windows as well as every new one. They come from UX reviews of these windows
(TcAgent 0.50) and keep the windows consistent, quiet and safe to use.

Reference implementations with the helpers for most of the rules:

| Topic | Where |
|---|---|
| Tabs, ⋯ menu, action lists (toolbar = context menu), enabled by selection, info bar, background calls, dialogs, empty states, state column | `vsbridge/KnowledgeWindow.cs` (`Act`/`A`/`DropDown`/`Main`, `Bar`, `MenuOf`, `UpdateGates`, `Cli`, `Info`, `Error`, `Dialog`, `Ask`, `Pick`, `Confirm`, `SearchBox`, `WithEmpty`, `Output`, `Columns` with `!` = state and `?` = check box) |
| Labelled toolbar, filter chips, progress line, master-detail like a report, check boxes in a tree | `vsbridge/TcUnitWindow.cs` |
| Narrow window, cards (goal / consequence / gate), state dot, running work as compact lines | `vsbridge/ChatView.cs`, `vsbridge/ChatCards.cs` |

## 1. New windows: mockup first

- A new window or a larger change of one starts with a **mockup** (HTML rendered to PNG, neutral example data) and
  the owner's approval - then it is built.
- After building: check it in **Visual Studio 2022 and the XAE Shell**, in the **light and the dark theme**, at a
  **narrow width** (about 400 px for tool windows).

## 2. Layout

- **Real tabs** on top (the selected one underlined); a **badge** counts what needs attention in a tab.
- Right of the tabs: short context as a link (e.g. "Platform: my-plc-platform ›"), **⟳** (F5) and a **⋯ menu** for
  rare and global actions (settings, backup/restore, set up, diagnostics, help, version). No action buttons between
  the tabs.
- Per tab from top to bottom: heading with a short, quieter explanation ("– …"), the main action, filters in their
  own row, the list, the details of the selection.
- **Master-detail:** the selected entry gets a **detail header** - name (larger), state chips, the fitting actions as
  links - then facts as label/value lines, then the **rendered content** (Markdown, a report-like layout, a diff in the
  compare window) instead of raw text. Long content (file preview, output) is folded while it is not needed.
- A topic of its own gets a tab of its own instead of being appended below another one.
- **Status line:** the last message on the left, what the tab holds (counts, time of reading) or a legend of the
  symbols on the right.
- **Narrow windows:** nothing important is cut off - hints wrap instead of ending in "…"; long output (a setup result,
  a list of paths) is one line in the info bar with **Details**; never centered multi-line text.

## 3. Actions

- At most **4-6 main actions** in a toolbar, as **icon + text**; everything else in the context menu, a drop-down
  ("Switch off ▾") or **More ▾**. The main action of a tab comes first and is highlighted (one per tab).
- **One list of actions per list:** toolbar and context menu show the same entries with the same names in the same
  order, groups separated by lines.
- **Vocabulary and order:** New … / Add existing … / Open / Open folder / Edit … / Rename … / Move to level … /
  Switch off ▾ · Switch on / Delete … / Copy path (Copy URL for links). For items from a source: **My changes** (yours
  against the installed version) and **What's new upstream** (the source since the install), Update and merge …,
  Revert to installed version …, Propose upstream (pull request) ….
- "…" only when a dialog or a question follows; "▾" for a drop-down.
- A button is **only enabled when it fits the selection** (re-evaluated on every selection change); the tooltip says
  what happens and the key.
- Actions on all entries go next to the heading, not between the actions on the selected entry.

## 4. Keyboard

| Key | Action |
|---|---|
| Double-click, Enter | Open |
| F2 | Rename |
| Del | Delete |
| Ctrl+C | Copy path (in a list) |
| F5 | Refresh |
| Ctrl+F | The search box of the tab |
| Space | Tick / untick the selected line |
| Esc | Cancel a dialog, "no" on a card, stop a running answer |

F5 and Ctrl+F are caught in `ToolWindowPane.PreProcessMessage` - otherwise the IDE takes them (start debugging, find).
Buttons can be reached with Tab.

## 5. States and colors

- A state is always a **symbol and a word**, never a color alone:
  ✓ applies / passed · ✗ failed · ⊘ switched off · ✎ changed locally · ↑ newer in the source · ↷ replaced ·
  ⟳ / ◐ running, starting · ○ not run, empty, not installed · ⚠ not reachable, needs attention.
- **Red only for errors** (failed, not reachable, missing). Changes, warnings and "worse than before" are orange;
  neutral states grey; running blue; good green.
- Numbers with too little data show **"—"** and a note ("needs 5 runs") instead of 0 % or a misleading trend.
- Columns that are empty for every line are hidden (width 0) and give their room to the others.
- **Only colors of the VS theme** (`EnvironmentColors`, `ThemedDialogColors`) or translucent tints that work on light
  and dark; test both themes.

## 6. Feedback and long actions

- Every action answers visibly: **success in the status line or the info bar** (with an optional link such as "Open
  folder"), never a dialog only to say "done". **Errors are always a dialog** (or, in the chat, a marked line/card that
  does not interrupt the stream).
- **Long actions never run on the UI thread:** TcAgent.exe, git, file scans and network calls run in the background
  (`await Cli(...)`) with a progress bar (thin bar or progress line) and the state in the status line; a long run can be
  **cancelled** (Stop). Values from the IDE (solution path, DTE) are read on the UI thread before.
- Long texts in a confirmation (a diff, a plan, a file list) are **scrollable**; for risky steps Enter cancels.
- Running work shows a **state dot** where it is chosen (● ready, ◐ starting / working, ● error) and every step as one
  compact line (◐ / ✓ / ✗, short result, time, details).

## 7. Lists, selection and empty states

- **Empty lists and views say what they are for and how to fill them** ("No source yet - New … adds a Git
  repository"); an empty chat shows suggestions to click; an empty chart says from when it shows something.
- **Several items are chosen with check boxes** whenever the choice takes effort (tests, items to install): the ticks
  stay when the filter or search changes; never Ctrl/Shift multi-selection - one click beside it would lose everything.
  "Select all" / "Select none" where it helps.
- Filters: kind, level, search box with a hint inside and ✕ to clear.

## 8. Safety-relevant information is visible

- What could do harm is shown before it happens: **scripts** in an item to install, the **license**, the files; the
  **gate card** before a runtime action (activate, PLC start/stop, ADS write); a permission card before a tool call
  outside the rules.
- Cards say **Goal** (what is wanted), **Consequence** (reads only / changes the project / acts on the PLC …) and
  **Gate** (why it asks), with one filled primary button (focused, Enter) and Esc for no; the answer stays visible on the
  card; a card closed from outside says why.
- Nothing runs without the user's explicit yes; no answer is a no.
- **Gate cards for runtime actions are safe by default:** Enter never confirms (the focus is on the "no" button, Enter and
  Esc cancel); the confirming button reacts only to a mouse click (no Click event, so neither the keyboard nor UI
  automation - an agent driving the IDE - can press it); the target (AMS Net ID) is bold, a target that is not this PC
  gets an extra warning; the card is scrolled into view; where no chat answer runs in a visible chat window the modal
  dialog (default No) stays.

## 9. Texts and privacy

- **Language:** German or English, by the answer language of the TcAgent Chat (without one: the language of Windows) -
  German when it is German, English for every other language. The same rule as for the texts the agent writes to the
  person (the question about the specification). Implemented in the Formatter settings and Format ST code
  (`vsbridge/FormatSettings.cs`, `T(en, de)`); the other windows are English for now and follow. Keys, file names and
  values in files (`formatting.json`, `preserve`) stay as they are; the window shows them in plain words.
- Texts short, the action before the explanation; plain words over jargon ("Source @ version", "newer in the source",
  kinds as words like "Coding rules", values like "keep as written" instead of `preserve`); the same terms as in the
  User Guide.
- **No user, computer, customer or project names** in texts, examples, screenshots and mockups - neutral examples only
  (`MAIN`, `FB_Motor`, `my-plc-platform`).
- **Paths shortened in the middle** (`D:\Projekte\…\POUs\MAIN.TcPOU`) and user folders as `%LOCALAPPDATA%`,
  `%USERPROFILE%`; the full path in the tooltip, Copy path copies it.

## 10. Dialogs

- Always `Dialog(...)`: colors of the IDE (ThemedDialogStyleLoader), owner = the IDE window, not in the taskbar,
  OK/Cancel at the bottom right, Enter = OK, Esc = Cancel, the focus in the first required field.
- **Checked while typing:** OK stays off until the input is valid, the reason below the field.
- Long choices with a filter (`Pick`); selection dialogs with search, a state column, details and check boxes (model:
  "Browse and install").
- Folders and files can always be chosen with **Browse …**; the path can also be typed.
