# Interface design

The dashboard uses a warm light workspace and a forest-green navigation rail. Sage is the primary status color; muted lilac and terracotta distinguish Copilot and Claude. Typography, spacing, and original SVG line icons establish the visual hierarchy. No remote fonts, images, or new runtime dependencies are required.

## Layout

- **Overview:** the target is a minimum, never a capacity limit. Below it, the dashboard says how many additional tasks are needed; at it, the minimum is met; above it, additional parallel work is positive. Filled segments grow with the running count, with darker green segments for tasks above the minimum. The display draws at most 20 segments and labels any further running tasks numerically. Percentages can exceed 100%.
- **Agent cards:** selecting a card filters both active tasks and completions. Selecting it again or pressing the filter button returns to all agents. The capacity summary always represents the whole workspace.
- **In progress:** task name, compact workspace label, live runtime, and latest output. The name and “View update” open a dialog with the full captured text and workspace path. Text is rendered literally, never executed as HTML.
- **Completed:** a title-sized disclosure control with a rotating SVG chevron. Five newest unread completions appear initially; “Show more” reveals five at a time, and “Show less” returns to five. Collapse preserves the page size and unread count. Clear all applies to the full inbox, including filtered or hidden entries.
- **Preferences:** target stepper and reminder interval remain alongside the task list on wide screens. Unsaved edits survive incoming snapshots. Browser reminders have an explicit opt-in button when supported.

Search matches task titles, workspace paths, agents, and captured output. It combines with the agent filter without changing the aggregate running count.

## Responsive behavior

The full navigation rail becomes an icon rail below 1000px, then compact top navigation below 600px. Preferences move below the task list at smaller widths. Text uses relative sizes: task updates and details are 16px at the default browser setting; primary task titles are 17px and supporting controls are generally 13–15px. Mobile agent cards stack vertically so text stays legible instead of shrinking. Dialogs fit the viewport. Controls use semantic buttons, visible keyboard focus, accessible names, and reduced-motion support.

## Live interactions

The connection indicator reflects the SSE connection and shows disconnection/retry state. Snapshot ordering guards against an older request overwriting a newer live update. Task list markup is retained when its contents have not changed, preserving focused controls. Elapsed and relative times update separately once per second.

Clear actions, task creation, and preference saves report request failures and refresh the snapshot after success. The redesign uses the existing adapters, settings, persisted inbox, and HTTP API.

## UI verification

Run the production service with `npm start`. For isolated interaction checks, run:

```sh
node scripts/preview-ui.mjs
```

The optional preview at [http://127.0.0.1:4318](http://127.0.0.1:4318) serves the same frontend with three synthetic running tasks and twelve completions. Its mutations exist only in memory; it never reads or writes real application history. Use it to exercise pagination, filters, task creation/completion, acknowledgement, settings, and mobile layouts. Stop the process to discard its data. It is not part of production startup.
