# FHQ Art Assets Group Upload Packages

Created: 2026-05-03 Asia/Shanghai
Purpose: independent small zip packages for uploading to group files. No GitHub upload is needed.

Source full package: `\\Gotham1\共享\黑客松\FHQ-art-assets-for-newton-20260503-080741`
Output folder: `\\Gotham1\共享\黑客松\FHQ-art-assets-group-upload-20260503`

## Upload These Zip Files

| Package | Size | Contents |
|---|---:|---|
| `00_handoff_docs_manifests_cursor_docs.zip` | 19.49 MB | handoff docs, manifests, Cursor/Codex art search docs |
| `01_current_fhq_integrated_assets.zip` | 320.88 MB | current FHQ integrated Unity assets with .meta files |
| `02_raw_downloaded_thirdparty_assets.zip` | 128.43 MB | raw downloaded third-party art/audio packs |
| `04a_downloads_new_scene_asset.zip` | 432.73 MB | large scene asset candidate pack |
| `04b_downloads_new_assets_mmd_character.zip` | 398.77 MB | Assets.zip, MMD4Mecanim zip, Ch36 character FBX animations |
| `04c_downloads_new_construction_school_tunnel.zip` | 361 MB | construction, school corridor, tunnel candidate packs |
| `05_newton_demo_correct_assets.zip` | 74.1 MB | Newton demo Unity Assets folder |
| `07_existing_project_demo_dependency_assets.zip` | 18.42 MB | optional existing project demo/dependency assets |

All zip files are independent archives. They are not .001/.002 split volumes, so each one can be uploaded/downloaded separately and extracted separately.

## Suggested Upload Order

- `00_handoff_docs_manifests_cursor_docs.zip`
- `01_current_fhq_integrated_assets.zip`
- `02_raw_downloaded_thirdparty_assets.zip`
- `04a_downloads_new_scene_asset.zip`
- `04b_downloads_new_assets_mmd_character.zip`
- `04c_downloads_new_construction_school_tunnel.zip`
- `05_newton_demo_correct_assets.zip`
- `07_existing_project_demo_dependency_assets.zip`

## Extraction

Create one destination folder, then extract every zip into that folder. If the unzip tool asks about merging folders, choose merge/yes. Do not overwrite newer edited project files unless you intentionally want the original asset package content.

## Integrity

Use `PACKAGE_SHA256.csv` to verify downloads if a group upload/download looks corrupted.

## License Note

This is for internal hackathon/team transfer. Some candidate packs still need license review before public redistribution or final release.
