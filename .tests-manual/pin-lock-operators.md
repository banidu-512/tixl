---
id: pin-lock-operators
title: PIN-Locking Operators
scope: graph-window
tags: [graph, selection, context-menu]
added: 2026-08-27
added-in-version: 4.3
prerequisites:
  - A project with a few connected operators on the graph canvas.
related-help:
  - ../.help/docs/using/LivePerformances.md
---

Verifies the operator PIN lock: locking a selection with a pinpad, the edit
guards while locked, and unlocking via the pinpad.

## Step: Locking a selection with a PIN

**Action:**
Select two operators, right-click and pick `Lock with PIN...`. Enter `1234` with the
keypad buttons or the keyboard and press `OK`, then enter `1234` again and press `OK`.

**Expected:**
- The pinpad dialog closes.
- Both operators show a magenta corner indicator on the graph and a small magenta
  lock glyph right of their title.
- Right-clicking the selection shows `Unlock...` instead of `Lock with PIN...`.

## Step: Editing is refused while locked

**Action:**
With the locked pair still selected, try in turn: drag them on the canvas, press
`Del`, press the Disable and Bypass shortcuts, open the context menu and check
`Rename`, `Delete`, `Align Select Left` and the `Display` entries, and select one
of them to inspect the Parameter window.

**Expected:**
- Dragging moves neither locked operator; other selected unlocked ops still move.
- `Del` does nothing; the selection stays intact.
- Disable / Bypass do not change their indicator lines.
- The listed context-menu entries are greyed out.
- The Parameter window is read-only: a header with the symbol name and namespace
  (no editable fields) plus the lock notice instead of parameters, name, namespace
  and tag buttons.

## Step: Rewiring is refused while locked

**Action:**
With an unlocked operator connected to a locked one, try in turn: drag a new cable
from the unlocked operator's output onto the locked operator's input anchor; grab an
existing cable at the locked operator's input or output to rip it away; click the
connection line between them to split-insert an operator.

**Expected:**
- No connection is created, removed or rewired in any of the three attempts.
- Wiring between two unlocked operators still works normally afterwards.

## Step: Timeline and undo cannot touch a locked op

**Action:**
Lock an operator that has animated parameters, select it and open the Timeline
window. Then press `Ctrl+Z` a few times on the graph.

**Expected:**
- The locked operator's keyframe curves do not appear on the dope sheet, selected
  or pinned.
- Locking cleared the undo history - `Ctrl+Z` does nothing right after locking.

## Step: Entry helpers — digit echo and Clear

**Action:**
Open `Unlock...` again, type `99` with the keyboard and watch the display, then
click `Clear`.

**Expected:**
- Each entered digit briefly appears next to the dots, then fades out.
- `Clear` wipes all entered digits at once (and disappears when the entry is
  already empty). `Del` still removes only the last digit.

## Step: Wrong PIN is rejected

**Action:**
Right-click the locked selection, pick `Unlock...`, enter `0000` and press `OK`,
then repeat two more times.

**Expected:**
- The dialog stays open, the display shakes and flashes, and the message line
  counts the remaining attempts down (`Wrong PIN - 2 attempts left...`).
- The operators remain locked.

## Step: Repeated wrong attempts enforce a timeout

**Action:**
Enter a wrong PIN a third time and press `OK`. Then (without closing the dialog)
wait for the countdown, enter the correct PIN and press `OK`.

**Expected:**
- After the third wrong attempt the keypad and OK go dim, input is ignored and
  the message line counts down (starting at 3 seconds).
- Once the countdown is over, the correct PIN unlocks as usual. Closing and
  reopening the dialog resets the attempt count.

## Step: Unlocking with the correct PIN

**Action:**
Enter `1234` and press `OK`.

**Expected:**
- The dialog closes and the magenta indicator disappears from both operators.
- Dragging, deleting and parameter editing work again.

## Step: The lock survives saving and reloading

**Action:**
Lock an operator again with a new PIN, save the project, reopen it, then try to
drag the operator.

**Expected:**
- The operator still shows the lock indicator and refuses to move.
- `Unlock...` with the new PIN releases it.
