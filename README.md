# Drafting Suite

Current version: `0.1.4`

Drafting Suite is a Tools by Raul Civil 3D drafting helper plugin. It is the home for managed replacements for high-use drafting AutoLISP routines.

## Commands

- `DS` opens the Drafting Suite palette.
- `DSFBKPREP` prepares an opened fieldbook drawing for drafting.
- `DSSETTINGS` opens Drafting Suite settings.
- `DSVERSION` prints the loaded Drafting Suite version.

## FBK Prep

`DSFBKPREP` is based on the current FBK drafting workflow:

1. Extract visible COGO point display graphics into ordinary CAD entities.
2. Burst nested anonymous block references until no anonymous blocks remain, up to the configured pass limit.
3. Convert text and mtext annotation to mleaders with the arrowhead at the original text insertion point and the mleader text placed `15` drawing units northeast.
4. Flatten drafting annotation to elevation `0`.
5. Set Civil 3D COGO point style and label style to `Standard` when those style properties are available.

The command asks whether to process the entire drawing or the current selection. The first version is intentionally conservative: it does not erase the drawing, does not detach references, and does not run the legacy `XCOGO` copy/undo/paste sequence.

Settings are stored in `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\settings.json`. Presets are JSON files stored in the configured preset folder, which defaults to `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\Presets`. The settings dialog can load, save, rename, delete, and set a default preset for client or template standards.
