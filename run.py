#!/usr/bin/env python3
"""
🔦 Lighthouse Bridge — Entry Point
OpenSim ↔ Matrix/Conduit Chat Bridge

Usage:
    python run.py                     # Default config.yaml
    python run.py --config my.yaml    # Custom config
"""
import argparse
from bridge.app import create_app

def main():
    parser = argparse.ArgumentParser(description="🔦 Lighthouse Bridge")
    parser.add_argument("--config", "-c", help="Path to config.yaml")
    args = parser.parse_args()

    app = create_app(config_path=args.config)
    cfg = app.config["cfg"]

    # Note: In production, use gunicorn or run two separate instances
    # for the two ports. For development, we bind to the opensim port.
    print(f"\n🔦 Lighthouse Bridge v0.1.0")
    print(f"   Listening on {cfg.opensim_host}:{cfg.opensim_port}")
    print(f"   AppService endpoint on {cfg.appservice_host}:{cfg.appservice_port}")
    print(f"   Press Ctrl+C to stop\n")

    app.run(
        host=cfg.opensim_host,
        port=cfg.opensim_port,
        debug=False,
    )

if __name__ == "__main__":
    main()
