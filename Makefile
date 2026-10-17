# The game install (Windows build, 1.7): defaults to the nearest folder at or above this one that holds
# Assembly-CSharp.dll or has a RimWorld/ subfolder that does (tools/rimworld-dir.sh).
ifndef RIMWORLD_DIR
RIMWORLD_DIR := $(shell tools/rimworld-dir.sh)
endif
ifeq ($(strip $(RIMWORLD_DIR)),)
ifneq ($(filter-out clean,$(or $(MAKECMDGOALS),build)),)
$(error No RimWorld install found at or above $(CURDIR) (or in a RimWorld/ subfolder of one); set RIMWORLD_DIR=/path/to/RimWorld)
endif
endif
export RIMWORLD_DIR

MOD_DIR      := Mods/ColonyScale
MOD_PROJ     := $(MOD_DIR)/Source/ColonyScale/ColonyScale.csproj
CHECK_PROJ   := $(MOD_DIR)/Source/ColonyScale.Tests/ColonyScale.Tests.csproj
FINGERPRINTS := $(MOD_DIR)/Fingerprints.tsv
BASELINE_IL  := tools/baseline/il
DIST_DIR     ?= $(CURDIR)/dist
CHECK        := dotnet run --project $(CHECK_PROJ) -c Release --no-build --

.PHONY: build build-check test fingerprints update decompile link bench dist clean

# ── Build ────────────────────────────────────────────
build:
	dotnet build $(MOD_PROJ) -c Release

build-check:
	dotnet build $(CHECK_PROJ) -c Release

# ── Test ─────────────────────────────────────────────
# Offline, no game needed: every patch target resolves, every transpiler finds its IL pattern,
# every patch applies, and every target still matches its recorded fingerprint.
test: build build-check
	$(CHECK) check --fingerprints $(FINGERPRINTS)

# Re-record fingerprints + baseline IL for the installed game. Run only after reviewing an
# update report (or after adding/changing an optimization).
fingerprints: build-check
	$(CHECK) fingerprints --write $(FINGERPRINTS) --game "$$($(CHECK) gameversion)"
	rm -rf $(BASELINE_IL) && $(CHECK) dump-il --out $(BASELINE_IL)

# ── Game update pipeline ─────────────────────────────
# After copying a new game version into $(RIMWORLD_DIR): decompile, diff every patched method
# against the baseline, rebuild, re-check, optionally benchmark; writes reports/update-<version>.md.
update:
	tools/update.sh

decompile: build-check
	tools/decompile.sh "$$($(CHECK) gameversion)"

# Make the game load this checkout (symlink in $(RIMWORLD_DIR)/Mods; no-op when overlaid on the install).
link:
	tools/link-mod.sh

# Headless A/B benchmark under portable Wine (see tools/headless/).
bench: build
	tools/headless/bench.sh

# ── Dist ─────────────────────────────────────────────
dist: test
	rm -rf $(DIST_DIR)/ColonyScale && mkdir -p $(DIST_DIR)/ColonyScale
	cp -r $(MOD_DIR)/About $(MOD_DIR)/Assemblies $(FINGERPRINTS) $(DIST_DIR)/ColonyScale/
	cd $(DIST_DIR) && rm -f ColonyScale.zip && python3 -m zipfile -c ColonyScale.zip ColonyScale
	@echo "Copy $(DIST_DIR)/ColonyScale/ (or unzip ColonyScale.zip) into RimWorld/Mods/ on the gaming PC"

clean:
	rm -rf $(MOD_DIR)/Assemblies $(MOD_DIR)/Source/*/bin $(MOD_DIR)/Source/*/obj
