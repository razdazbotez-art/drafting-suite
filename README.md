# Drafting Suite

Current version: `0.1.40`

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
3. Optionally explode regular named block references in the generated extraction output before anonymous block bursting, using the configured pass count. This is off by default.
4. Burst nested anonymous block references until no anonymous blocks remain, up to the configured pass limit. Named dynamic blocks are not treated as anonymous even when AutoCAD stores their current state in an anonymous internal block record. Visible attributes are converted to text, attribute definitions are discarded, and the source block is kept if no replacement objects are created.
5. Optionally explode regular named block references again after bursting, using the configured pass count. This is off by default, and the source block is kept if no replacement objects are created.
6. Preserve generated block output instead of deleting it by layer rule.
7. Delete original or generated regular AutoCAD line entities when their layer matches the configured COGO point layer list.
8. Convert remaining regular AutoCAD line entities to two-vertex 3D polylines.
9. Delete extracted text and eligible original text below the configured tiny text height. Text at exactly the configured height is kept.
10. Delete extracted text and eligible original text when its layer matches a configured delete text layer wildcard.
11. Leave extracted text and eligible original text as-is when its layer matches a configured keep-as-text layer wildcard. When inverse keep-as-text mode is enabled, non-matching layers are kept as text and matching layers are forced to convert to MLeaders. Tiny text below the configured height is still deleted first.
12. Convert remaining text and mtext annotation to mleaders with the arrowhead at the original text insertion point and the mleader text placed `15` drawing units northeast.
13. Flatten drafting annotation to elevation `0`, except block names matching the configured do-not-flatten wildcard list.
14. Set surviving created, converted, flattened, or kept annotation objects to ByLayer for color, linetype, and lineweight.
15. Set Civil 3D COGO point style and label style to `Standard` when those styles are available.

The command asks whether to process the entire drawing or the current selection. The extraction stage operates on generated output objects rather than deleting original drawing objects the way the legacy `XCOGO` undo/copy/paste sandbox did. Original drawing annotation is only eligible for text deletion, text-to-mleader conversion, or flattening when its layer matches the configured annotation layer list.

Settings are stored in `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\settings.json`. Presets are JSON files stored in the configured preset folder, which defaults to `%LOCALAPPDATA%\Civil3D_Plugins\DraftingSuite\Presets`. The settings dialog can load, save, rename, delete, and set a default preset for client or template standards.
