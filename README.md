# Drafting Suite

Current version: `0.1.20`

Drafting Suite is a Tools by Raul Civil 3D drafting helper plugin. It is the home for managed replacements for high-use drafting AutoLISP routines.

## Commands

- `DS` opens the Drafting Suite palette.
- `DSFBKPREP` prepares an opened fieldbook drawing for drafting.
- `DSSETTINGS` opens Drafting Suite settings.
- `DSVERSION` prints the loaded Drafting Suite version.

## FBK Prep

`DSFBKPREP` is based on the current FBK drafting workflow:

1. Extract visible COGO point display graphics into ordinary CAD entities.
2. Delete inserted Civil 3D survey network drawing objects when enabled.
3. Explode regular block references before anonymous block bursting, using the configured pass count.
4. Burst nested anonymous block references until no anonymous blocks remain, up to the configured pass limit.
5. Explode regular block references again after bursting, using the configured pass count.
6. Delete extracted COGO graphics whose layers do not match the configured result or annotation layer wildcards.
7. Convert regular AutoCAD line entities to two-vertex 3D polylines unless their layer matches a protected source wildcard.
8. Delete text and mtext at or below the configured tiny text height.
9. Delete text and mtext when its layer matches a configured delete text layer wildcard.
10. Leave text and mtext as-is when its layer matches a configured keep-as-text layer wildcard.
11. Convert remaining text and mtext annotation to mleaders with the arrowhead at the original text insertion point and the mleader text placed `15` drawing units northeast.
12. Flatten drafting annotation to elevation `0`, except protected source layers and block names matching the configured do-not-flatten wildcard list.
13. Set Civil 3D COGO point style and label style to `Standard` when those styles are available.

The command asks whether to process the entire drawing or the current selection. The first version is intentionally conservative: it does not erase the drawing, does not detach references, and does not run the legacy `XCOGO` copy/undo/paste sequence.

Settings are stored in `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\settings.json`. Presets are JSON files stored in the configured preset folder, which defaults to `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\Presets`. The settings dialog can load, save, rename, delete, and set a default preset for client or template standards.
