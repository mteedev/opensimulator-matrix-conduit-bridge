-- ============================================================
-- Lighthouse Bridge — Database Schema
-- Port of Fiona Sweet's schema.sql
-- ============================================================
-- Run this on your bridge database:
--   mysql -u root -p < schema.sql
--
-- Note: os_groups_membership and os_groups_roles tables
-- must be accessible (same DB, replication, or remote access)
-- ============================================================

CREATE DATABASE IF NOT EXISTS `opensim_matrix_bridge`
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

USE `opensim_matrix_bridge`;

-- Bridge state: which OpenSim groups are linked to which Matrix rooms
CREATE TABLE IF NOT EXISTS `group_bridge_state` (
  `group_uuid` char(36) NOT NULL,
  `enabled` tinyint(1) DEFAULT 0,
  `room_id` varchar(128) DEFAULT NULL,
  `enabled_by` char(36) DEFAULT NULL,
  `enabled_at` datetime DEFAULT NULL,
  PRIMARY KEY (`group_uuid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Avatar to Matrix ID mapping (puppet user cache)
CREATE TABLE IF NOT EXISTS `avatar_mxid_map` (
  `avatar_uuid` char(36) NOT NULL,
  `mxid` varchar(128) NOT NULL,
  `display_name` varchar(128) DEFAULT NULL,
  `created_at` datetime DEFAULT current_timestamp(),
  PRIMARY KEY (`avatar_uuid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Group-to-room mapping (may duplicate bridge_state — kept for compat)
CREATE TABLE IF NOT EXISTS `group_room_map` (
  `group_uuid` char(36) NOT NULL,
  `room_id` varchar(128) NOT NULL,
  `room_alias` varchar(128) DEFAULT NULL,
  `created_at` datetime DEFAULT current_timestamp(),
  PRIMARY KEY (`group_uuid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Event deduplication
CREATE TABLE IF NOT EXISTS `dedupe_events` (
  `event_id` varchar(128) NOT NULL,
  `seen_at` datetime DEFAULT current_timestamp(),
  PRIMARY KEY (`event_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Invite codes (for sharing Matrix room access)
CREATE TABLE IF NOT EXISTS `room_invites` (
  `invite_code` varchar(32) NOT NULL,
  `group_uuid` char(36) NOT NULL,
  `room_id` varchar(128) NOT NULL,
  `created_by` char(36) NOT NULL,
  `expires_at` datetime DEFAULT NULL,
  `uses_remaining` int(11) DEFAULT 1,
  `created_at` datetime DEFAULT current_timestamp(),
  PRIMARY KEY (`invite_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ============================================================
-- OpenSim group tables (must exist — from your OpenSim DB)
-- If bridge DB is separate, set up replication or remote access
-- ============================================================

CREATE TABLE IF NOT EXISTS `os_groups_membership` (
  `GroupID` char(36) NOT NULL DEFAULT '',
  `PrincipalID` varchar(255) NOT NULL DEFAULT '',
  `SelectedRoleID` char(36) NOT NULL DEFAULT '',
  `Contribution` int(11) NOT NULL DEFAULT 0,
  `ListInProfile` int(4) NOT NULL DEFAULT 1,
  `AcceptNotices` int(4) NOT NULL DEFAULT 1,
  `AccessToken` char(36) NOT NULL DEFAULT '',
  PRIMARY KEY (`GroupID`,`PrincipalID`),
  KEY `PrincipalID` (`PrincipalID`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `os_groups_roles` (
  `GroupID` char(36) NOT NULL DEFAULT '',
  `RoleID` char(36) NOT NULL DEFAULT '',
  `Name` varchar(255) NOT NULL DEFAULT '',
  `Description` varchar(255) NOT NULL DEFAULT '',
  `Title` varchar(255) NOT NULL DEFAULT '',
  `Powers` bigint(20) unsigned NOT NULL DEFAULT 0,
  PRIMARY KEY (`GroupID`,`RoleID`),
  KEY `GroupID` (`GroupID`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;
