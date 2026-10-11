"""Software Siemens S7 PLC for local development and demos (no hardware needed).

Serves one data block over the S7 protocol and simulates "part complete" signals
using the same layout as the default appsettings.json:

    DBX0.0  complete bit
    DBD2    completion sequence counter (Handshake mode only, big-endian DWORD)
    DBW6    heartbeat word, written by the dashboard

Usage:
    python plc_sim.py                    # Edge mode, DB100 on 127.0.0.1:102
    python plc_sim.py --mode handshake   # Handshake mode (set Plc:SampleMode=Handshake)
"""

import argparse
import random
import struct
import time

from snap7.server import Server
from snap7.type import SrvArea


def main() -> None:
    parser = argparse.ArgumentParser(description="Software S7 PLC for ProductionDashboard")
    parser.add_argument("--mode", choices=["edge", "handshake"], default="edge")
    parser.add_argument("--host", default="127.0.0.1", help="listen address (default: 127.0.0.1)")
    parser.add_argument("--db", type=int, default=100, help="data block number (default: 100)")
    parser.add_argument("--min-interval", type=float, default=1.5, help="min seconds between parts")
    parser.add_argument("--max-interval", type=float, default=4.0, help="max seconds between parts")
    parser.add_argument("--pulse-ms", type=int, default=350, help="Edge mode: how long the bit stays high")
    args = parser.parse_args()

    db = bytearray(8)
    server = Server()
    server.register_area(SrvArea.DB, args.db, db)
    server.start_to(args.host)
    print(f"S7 PLC simulator: {args.mode} mode, DB{args.db} on {args.host}:102 (Ctrl+C to stop)", flush=True)

    seq = 0
    parts = 0
    try:
        while True:
            time.sleep(random.uniform(args.min_interval, args.max_interval))
            if args.mode == "edge":
                db[0] |= 0x01
                time.sleep(args.pulse_ms / 1000)
                db[0] &= 0xFE
            else:
                # The dashboard clears the bit after persisting; the counter covers any
                # completions that happen before it does, so nothing is lost.
                seq = 0 if seq >= 0xFFFFFFFF else seq + 1
                db[2:6] = struct.pack(">I", seq)
                db[0] |= 0x01
            parts += 1
            print(f"\rparts: {parts}", end="", flush=True)
    except KeyboardInterrupt:
        print()
    finally:
        server.stop()
        server.destroy()


if __name__ == "__main__":
    main()
