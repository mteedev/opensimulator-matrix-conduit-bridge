# 🔦 Lighthouse Bridge

**OpenSimulator ↔ Matrix/Conduit Chat Bridge**

A Python/Flask port of [Fiona Sweet's opensim-matrix-bridge](https://codeberg.org/fionasweet/opensim-matrix-bridge), rewritten to run on [Conduit](https://conduit.rs) instead of Synapse.

## What It Does

Lighthouse Bridge connects your OpenSimulator grid's group chat to Matrix rooms with full bidirectional communication:

- **OpenSim → Matrix**: In-world group chat messages appear in Matrix rooms via puppet users (each OpenSim avatar gets their own Matrix identity with proper name and profile photo)
- **Matrix → OpenSim**: Messages from Matrix/Element users appear in OpenSim group chat with a `[Matrix]` tag
- **Full puppet support**: OpenSim avatars show up as real Matrix users, not just bot relay messages
- **Power level sync**: OpenSim group owners/officers get corresponding Matrix power levels
- **Avatar photos**: Profile pictures sync from OpenSim to Matrix

## Why Lighthouse?

| | Fiona's Original | Lighthouse |
|---|---|---|
| **Language** | C# | Python/Flask |
| **Matrix Server** | Synapse (1-2GB RAM) | Conduit (128MB RAM) |
| **Database** | MariaDB + PostgreSQL | MariaDB only |
| **Hosting** | Heavy VPS | Light VPS |

Same features, fraction of the resources.

## Quick Start

```bash
# Clone
git clone https://github.com/YOUR_USERNAME/lighthouse-bridge.git
cd lighthouse-bridge

# Setup
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt

# Configure
cp config.example.yaml config.yaml
# Edit config.yaml with your settings

# Database
mysql -u root -p < schema.sql

# Register AppService with Conduit (in admin room):
#   @conduit:your.server: register_appservice
#   (paste contents of opensim-bridge.yaml)

# Run
python run.py
```

## Architecture

```
OpenSim Region Server          Lighthouse Bridge           Conduit (Matrix)
┌────────────────────┐    ┌───────────────────────┐    ┌──────────────────┐
│ MatrixGroupInject  │───▶│ POST /os/event        │    │                  │
│ Module.cs          │    │                       │───▶│ Matrix Rooms     │
│                    │◀───│ /matrix/group-message │    │ (via puppets)    │
│                    │    │                       │◀───│                  │
└────────────────────┘    │ PUT /_matrix/app/v1/  │    │ AppService Push  │
                          │     transactions      │    │                  │
                          └───────────────────────┘    └──────────────────┘
```

## OpenSim Setup

The OpenSim side requires `MatrixGroupInjectModule.cs` compiled into your region server. See the `opensim/` directory for the module and configuration instructions.

Add to `OpenSim.ini`:
```ini
[MatrixBridge]
    Enabled = true
    BridgeUrl = "http://your-bridge-server:9010/os/event"
    SharedSecret = "your-shared-secret"
```

## Linking a Group

```bash
# Step 1: Create group in-world (via Firestorm)
# Step 2: Enable bridge
curl -X POST http://localhost:9010/admin/bridge/enable \
  -H "Content-Type: application/json" \
  -d '{"GroupUuid":"GROUP-UUID","GroupName":"My Group","FounderAvatarUuid":"YOUR-UUID"}'

# Step 3: Invite yourself to the Matrix room
curl -X POST \
  -H "Authorization: Bearer YOUR_AS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"user_id":"@you:your.server"}' \
  'http://localhost:6167/_matrix/client/v3/rooms/ROOM_ID/invite'
```

## Credits

- **Original bridge**: [Fiona Sweet](mailto:fiona@pobox.holoneon.com) — [opensim-matrix-bridge](https://codeberg.org/fionasweet/opensim-matrix-bridge)
- **Conduit port**: Shwartzie (Gundahar Bravin) — [Neverworld Grid](https://neverworldgrid.com)
- **License**: GPL-3.0 (bridge code) + BSD (OpenSim module, per Fiona's original)
