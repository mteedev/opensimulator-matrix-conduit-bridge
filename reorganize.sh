#!/bin/bash
# ============================================================
# 🔦 Lighthouse Bridge — Repo Reorganization Script
# Run this from inside your cloned repo:
#   cd opensimulator-matrix-conduit-bridge
#   bash reorganize.sh
# ============================================================

set -e
echo "🔦 Reorganizing repo for Lighthouse Bridge..."

# --- Step 1: Delete Fiona's C#/Synapse/PHP files ---
echo ""
echo "Step 1: Removing C#/Synapse bridge files (replaced by Python)..."

git rm -f GroupBridgeService.cs       2>/dev/null || true
git rm -f Program.cs                  2>/dev/null || true
git rm -f opensim-matrix-bridge.csproj 2>/dev/null || true
git rm -f compile.sh                  2>/dev/null || true
git rm -f opensim-matrix-bridge.service 2>/dev/null || true
git rm -f av.php                      2>/dev/null || true
git rm -f pdo-only.php                2>/dev/null || true
git rm -f Step-by-Step.txt            2>/dev/null || true
git rm -f LICENSE.txt                 2>/dev/null || true

echo "  ✓ C#/PHP files removed"

# --- Step 2: Keep OpenSim C# modules (they stay) ---
echo ""
echo "Step 2: OpenSim modules stay as-is:"
echo "  ✓ OpenSim/Addons/Groups/MatrixGroupInjectModule.cs"
echo "  ✓ OpenSim/Services/HypergridService/HGInstantMessageService.cs"

# --- Step 3: Create directory structure ---
echo ""
echo "Step 3: Creating Python project structure..."

mkdir -p bridge
mkdir -p scripts
mkdir -p data

echo "  ✓ Directories created"

# --- Step 4: Prompt to copy new files ---
echo ""
echo "Step 4: Now copy the Lighthouse files into the repo:"
echo ""
echo "  Copy these files from Claude's output into the repo root:"
echo "    bridge/__init__.py"
echo "    bridge/app.py"
echo "    bridge/config.py"
echo "    bridge/service.py"
echo "    config.example.yaml"
echo "    requirements.txt"
echo "    run.py"
echo "    scripts/lighthouse-bridge.service"
echo ""
echo "  Then REPLACE these existing files with the new versions:"
echo "    README.md"
echo "    opensim-bridge.yaml"
echo "    schema.sql"
echo "    .gitignore"
echo ""

# --- Step 5: Stage and commit ---
echo "After copying files, run:"
echo ""
echo "  git add -A"
echo '  git commit -m "Rewrite: Python/Flask port for Conduit (credit: Fiona Sweet original)"'
echo "  git push origin main"
echo ""
echo "🔦 Done! Your repo is ready for Lighthouse Bridge."
