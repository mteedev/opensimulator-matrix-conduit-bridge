# OpenSim-Matrix Chat Bridge

A bridge system that integrates OpenSim group chat rooms with Matrix rooms, enabling bidirectional communication between OpenSim users and Matrix users.

## Overview

This bridge allows OpenSim grid users with group creation privileges to create corresponding Matrix rooms for their groups. Messages sent in Matrix appear in OpenSim Group Chat and vice versa.

### Features

- Bidirectional message synchronization between OpenSim Group Chat and Matrix rooms
- OpenSim users displayed with name, UUID, and avatar profile photo in Matrix
- Matrix users displayed with name and [Matrix] tag in OpenSim Group Chat
- Invite-based room access (public discovery/join not recommended to prevent spam)

### Limitations

- Images and profiles are not displayed in OpenSim Group Chat (plain text only)
- Public room discovery should be avoided to prevent spam

## Architecture

The system consists of two main components:

1. **Matrix Bridge** - Handles Matrix-side integration
2. **OpenSim Injector** - Handles OpenSim-side integration

## Setup Instructions

### Prerequisites

Generate three cryptographic keys using OpenSSL:

```bash
openssl rand -hex 32
```

You will need keys for:
- `AS_TOKEN` (AppService Token)
- `HS_TOKEN` (Homeserver Token)
- `OS_BRIDGE_SECRET` (OpenSim Bridge Secret)

### Matrix/Synapse Configuration

#### Installation Notes

- Run Matrix/Synapse on a separate machine (not the same as Robust server)
- PostgreSQL is recommended over SQLite3
- MySQL/MariaDB cannot be used for Synapse due to encoding issues

#### Database Configuration

In `homeserver.yaml`:

```yaml
database:
  name: psycopg2
```

#### AppService Configuration

Add the appservice configuration file path:

```yaml
app_service_config_files:
  - /etc/matrix-synapse/appservices/opensim-bridge.yaml
```

See the included `opensim-bridge.yaml` file and update it with your homeserver domain.

### Bridge Database Setup

The bridge uses MariaDB (MySQL should also work). See the included `schema.sql` file for database schema.

#### Group Tables

Two group tables are required:
- `os_groups_membership`
- `os_groups_roles`

Choose one of the following approaches:

1. Direct database access from Matrix server to OpenSim database
2. Scheduled script to copy data periodically
3. MariaDB replication setup

### OpenSim/Robust Configuration

#### Source Code Setup

1. Create a new branch to maintain clean master source and merge upstream updates
2. Add required files:
   - `OpenSim/Addons/Groups/MatrixGroupInjectModule.cs` (Region file, required)
   - `OpenSim/Services/HypergridService/HGInstantMessageService.cs` (Robust file, required only if supporting off-grid group members)
3. Build as normal

#### Configuration Files

Add to both `Robust.HG.ini` and `OpenSim.ini` if using both:

```ini
[MatrixBridge]
    Enabled = true
    BridgeUrl = "http://10.99.0.35:9010/os/event"
    SharedSecret = "SECRETKEY"
```

Update `BridgeUrl` to your bridge address and `SharedSecret` to your generated key.

#### OpenSim.ini Settings

```ini
[Messaging]
    OfflineMessageModule = "Offline Message Module V2"
    StorageProvider = OpenSim.Data.MySQL.dll
    ForwardOfflineGroupMessages = true

[Groups]
    Module = "Groups Module V2"
    StorageProvider = OpenSim.Data.MySQL.dll
    ServicesConnectorModule = "Groups HG Service Connector"
    LocalService = remote
    GroupsServerURI = ${Const|PrivURL}:${Const|PrivatePort}
    MessagingEnabled = true
    MessagingModule = "Groups Messaging Module V2"
    NoticesEnabled = true
    MessageOnlineUsersOnly = true
```

#### Robust.HG.ini Settings

```ini
[ServiceList]
    GroupsServiceConnector = "${Const|PrivatePort}/OpenSim.Addons.Groups.dll:GroupsServiceRobustConnector"
    HGGroupsServiceConnector = "${Const|PublicPort}/OpenSim.Addons.Groups.dll:HGGroupsServiceRobustConnector"

[LoginService]
    SRV_IMServerURI = "http://hg.holoneon.com:80/"
    SRV_GroupsServerURI = "http://hg.holoneon.com:80/"

[HGInstantMessageService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGInstantMessageService"
    GridService = "OpenSim.Services.GridService.dll:GridService"
    PresenceService = "OpenSim.Services.PresenceService.dll:PresenceService"
    UserAgentService = "OpenSim.Services.HypergridService.dll:UserAgentService"
    InGatekeeper = True

[Messaging]
    OfflineIMService = ParentalControlsRobust.dll:ParentalOfflineIMService

[Groups]
    OfflineIMService = ParentalControlsRobust.dll:ParentalOfflineIMService
    UserAccountService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
```

### Avatar Profile Photos

See the included `av.php` file for an example implementation of avatar profile photo handling.

## Administration

### Authentication Tokens

- `AS_TOKEN` - AppService authentication to Synapse
- `HS_TOKEN` - Synapse to Bridge authentication
- `USER_ACCESS_TOKEN` - Regular Matrix user access token
- `ADMIN_TOKEN` - Synapse admin API access token
- `OS_BRIDGE_SECRET` - Bridge to OpenSim authentication

### API Commands

Note: Replace `matrix.holoneon.com` with your homeserver domain, `ROOM_ID` with actual room IDs, and tokens with your actual tokens.

#### View Full Room State (AppService Bot)

```bash
curl -H "Authorization: Bearer AS_TOKEN" \
  'http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/state'
```

Fetches complete state of a Matrix room including power levels, members, and topic.

#### Force AppService Bot to Join Room

```bash
curl -X POST \
  -H "Authorization: Bearer AS_TOKEN" \
  'http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/join?user_id=@opensim_bot:matrix.holoneon.com'
```

Forces the bridge bot to join a room if it was removed or never joined.

#### View Room State (Normal User)

```bash
curl -H "Authorization: Bearer USER_ACCESS_TOKEN" \
  'http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/state'
```

Checks what a specific Matrix user can see in the room.

#### Log In Matrix User

```bash
curl -X POST http://127.0.0.1:8008/_matrix/client/v3/login \
  -H "Content-Type: application/json" \
  -d '{
        "type": "m.login.password",
        "user": "USERNAME",
        "password": "PASSWORD"
      }'
```

Authenticates a Matrix user and returns an access token.

#### Invite User to Room (AppService Bot)

```bash
curl -X POST \
  -H "Authorization: Bearer AS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"user_id":"@fiona:matrix.holoneon.com"}' \
  'http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/invite'
```

Invites a Matrix user to the bridged OpenSim room.

#### List Room Members

```bash
curl -H "Authorization: Bearer USER_ACCESS_TOKEN" \
  'http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/members'
```

Lists all current members in the room.

#### Register Puppet User (AppService)

```bash
curl -X POST \
  -H "Authorization: Bearer AS_TOKEN" \
  -H "Content-Type: application/json" \
  'http://127.0.0.1:8008/_matrix/client/v3/register?kind=user' \
  -d '{
        "type": "m.login.application_service",
        "username": "os_UUIDWITHOUTDASHES"
      }'
```

Creates a Matrix puppet user owned by the AppService for an OpenSim avatar.

#### Enable Bridge for OpenSim Group

```bash
curl -X POST http://10.99.0.5:9010/admin/bridge/enable \
  -H "Content-Type: application/json" \
  -d '{
        "GroupUuid": "GROUP_UUID",
        "GroupName": "GROUP_NAME",
        "FounderAvatarUuid": "FOUNDER_UUID"
      }'
```

Creates and configures a Matrix room linked to an OpenSim group.

#### Verify Room Alias Mapping

```bash
curl -H "Authorization: Bearer AS_TOKEN" \
  'http://127.0.0.1:8008/_matrix/client/v3/directory/room/%23os_GROUPSHORT:matrix.holoneon.com'
```

Confirms that the OpenSim group alias resolves to the correct Matrix room ID.

#### Force-Join User (Synapse Admin API)

```bash
curl -X POST \
  -H "Authorization: Bearer ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"user_id":"@fiona:matrix.holoneon.com"}' \
  'http://127.0.0.1:8008/_synapse/admin/v1/join/!ROOM_ID'
```

Forces a Matrix user into a room bypassing normal join rules.

#### Invite User to Room (Generic)

```bash
curl -X POST \
  -H "Authorization: Bearer USER_ACCESS_TOKEN_OR_AS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"user_id":"@USER:matrix.holoneon.com"}' \
  http://127.0.0.1:8008/_matrix/client/v3/rooms/!ROOM_ID/invite
```

Generic invite command for any Matrix user.

#### Resync Group Metadata

```bash
curl -X POST http://10.99.0.5:9010/admin/bridge/resync \
  -H "Content-Type: application/json" \
  -H "X-Bridge-Secret: OS_BRIDGE_SECRET" \
  -d '{
        "GroupUuid": "GROUP_UUID"
      }'
```

Forces a full re-sync of avatars, display names, and power levels without deleting the room.


## Support

Fiona Sweet <fiona@pobox.holoneon.com>


