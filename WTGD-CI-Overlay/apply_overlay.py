from pathlib import Path
import shutil
import sys

repo = Path(__file__).resolve().parents[1]
mod = repo / "WTGD-1.5-Port"
source = mod / "Source"

utility_src = Path(__file__).with_name("ManifestDeitiesUninstallUtility.cs")
if not utility_src.exists():
    raise SystemExit("missing uninstall utility overlay")
shutil.copy2(utility_src, source / utility_src.name)

# Add a state reset method to the GameComponent. The caller removes the component
# from Game.components after cleanup so it is not serialized into the cleaned save.
p = source / "DivineFavorManager.cs"
s = p.read_text(encoding="utf-8-sig")
needle = """        public override void ExposeData()\n        {\n"""
insert = """        public void PrepareForUninstall()\n        {\n            records?.Clear();\n            activeInvocations?.Clear();\n            prayerRecords?.Clear();\n        }\n\n        public override void ExposeData()\n        {\n"""
if "public void PrepareForUninstall()" not in s:
    if needle not in s:
        raise SystemExit("DivineFavorManager insertion point not found")
    s = s.replace(needle, insert, 1)
p.write_text(s, encoding="utf-8")

# Add the uninstall action at the TOP of the settings page. V1.1 placed it at
# the bottom of a non-scrollable Listing_Standard, which made it invisible on
# common UI scales/resolutions.
p = source / "ManifestDeitiesSettings.cs"
s = p.read_text(encoding="utf-8-sig")
needle = """            Listing_Standard listing = new Listing_Standard();\n            listing.Begin(inRect);\n"""
replace = """            Listing_Standard listing = new Listing_Standard();\n            listing.Begin(inRect);\n            listing.Label(\"MD_UninstallSection\".Translate());\n            if (Widgets.ButtonText(listing.GetRect(34f), \"MD_PrepareUninstallButton\".Translate()))\n            {\n                ManifestDeitiesUninstallUtility.RequestPrepareForUninstall();\n            }\n            listing.GapLine();\n"""
if "MD_PrepareUninstallButton" not in s:
    if needle not in s:
        raise SystemExit("ManifestDeitiesSettings insertion point not found")
    s = s.replace(needle, replace, 1)
else:
    # Defensive migration in case a future source snapshot already has the V1.1
    # bottom placement.
    old = """            listing.Gap(10f);\n            listing.Label(\"MD_UninstallSection\".Translate());\n            if (Widgets.ButtonText(listing.GetRect(34f), \"MD_PrepareUninstallButton\".Translate()))\n            {\n                ManifestDeitiesUninstallUtility.RequestPrepareForUninstall();\n            }\n"""
    s = s.replace(old, "", 1)
    if "listing.Begin(inRect);\n            listing.Label(\"MD_UninstallSection\".Translate());" not in s:
        s = s.replace(needle, replace, 1)
p.write_text(s, encoding="utf-8")

print("WTGD 1.5 uninstall overlay applied (V1.2 visible-top settings action)")
