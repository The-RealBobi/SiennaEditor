# Sienna Editor changelog

## v1.2.0

- Added JSON, XML and YAML export to the editor, batch export and command line.
- Added OneForAll to combine documents into portable batchCFG files and extract them as CFG, CFGBIN, JSON, XML or YAML.
- Added support for legacy batchCFG files, drag and drop, and exporting only modified documents when the batch has a comparison baseline.
- Added overwrite confirmation for OneForAll and fixed extraction paths on Windows.
- Added Find and Replace in the editor with match navigation, case and whole-word options, regular expressions and Undo. Added a literal replacement command to the CLI.
- Added tab actions to duplicate a document or show its source in the file manager.
- Added inline entry renaming and disabled structural actions when the document cannot safely perform them.
- Replaced toolbar labels with accessible icons and made confirmation dialogs size to their content.
- Assigned Ctrl/Cmd+Shift+S to Save As and Alt+S to Save All.
- Updated to CFGBINManager 1.0.6 with 64-bit T2B support and corrected Victory Road event trees.

## v1.1.2

- Shortened the time the horizontal scrollbar stays expanded after use in long field values.
- Simplified Structure settings and renamed the import button to Import Structure.
- Moved file labels next to their paths in Conflict Merge and removed redundant prompts.
- Shortened search labels and removed the entries counter, Fields heading and routine status messages.
- Removed introductory copy and the repeated Structure label from Batch Export.

## v1.1.1

- Updated how the editor saves T2B binaries and exports readable CFG files.
